using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Burmalda.ConcurrencyLab.Api;
using Microsoft.Extensions.Configuration;

namespace Burmalda.PostgresControl;

// A deliberately small Engine adapter for the existing fixed lab operations.
// No shell, Compose executable, host paths or host process IDs are used in Docker mode.
public sealed class DockerEngineClient
{
    private readonly HttpClient http;
    private readonly string project;
    private readonly IConfiguration? configuration;
    public DockerEngineClient(IConfiguration config)
    {
        configuration = config;
        project = config["Docker:Project"] ?? "burmalda";
        var socketPath = config["Docker:Socket"] ?? "/var/run/docker.sock";
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            }
        };
        http = new(handler) { BaseAddress = new("http://docker"), Timeout = TimeSpan.FromMinutes(3) };
    }

    // Injectable transport allows tests to exercise identity checks and daemon-side lifetimes.
    public DockerEngineClient(HttpClient http, string project = "burmalda") { this.http = http; this.project = project; }

    private async Task<JsonNode?> Send(HttpMethod method, string path, object? body, CancellationToken ct, bool missing = false)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, ct);
        if (missing && response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode == HttpStatusCode.NotModified) return null; // idempotent start/stop
        if (!response.IsSuccessStatusCode)
            throw new ReplicationLabException(503, $"Docker Engine: операция не выполнена (HTTP {(int)response.StatusCode}). Проверьте журнал контейнера и доступ к socket.");
        var text = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }
    private Task<JsonNode?> Get(string path, CancellationToken ct, bool missing = false) => Send(HttpMethod.Get, path, null, ct, missing);
    private Task<JsonNode?> Post(string path, object? body, CancellationToken ct) => Send(HttpMethod.Post, path, body, ct);
    private static string Segment(string value) => Uri.EscapeDataString(value);
    private static void Record(string kind, string id) => ExecutionScope.Current?.DockerStarted(new(kind, id));

    private async Task<JsonNode> Owned(string name, CancellationToken ct, bool maintenance = false)
    {
        var info = await Get($"/containers/{Segment(name)}/json", ct) ?? throw new ReplicationLabException(503, "Контейнер не найден.");
        var labels = info["Config"]?["Labels"];
        var service = labels?["com.docker.compose.service"]?.GetValue<string>();
        var actualName = info["Name"]?.GetValue<string>();
        var database = service is "postgres" or "postgres-replica";
        var raft = service is not null && Enumerable.Range(1, 5).Any(id => service == $"raft-node-{id}");
        var valid = labels?["com.docker.compose.project"]?.GetValue<string>() == project &&
            (maintenance ? database && labels?["burmalda.raft.operation"] is not null :
                (database || raft) && actualName == $"/{project}-{service}");
        if (!valid) throw new ReplicationLabException(409, "Контейнер не принадлежит разрешённому сервису проекта.");
        if (maintenance && (!Guid.TryParse(labels?["burmalda.raft.operation"]?.GetValue<string>(), out var operation) ||
            actualName != $"/{project}-raft-maintenance-{operation:N}" ||
            !string.Equals(labels?["com.docker.compose.oneoff"]?.GetValue<string>(), "true", StringComparison.OrdinalIgnoreCase)))
            throw new ReplicationLabException(409, "Не подтверждена принадлежность maintenance-контейнера операции.");
        return info;
    }
    public async Task ManageRaft(int id, string action, CancellationToken ct)
    {
        if (id is < 1 or > 5 || action is not ("Start" or "Stop" or "Restart")) throw new ArgumentException("Invalid node action.");
        var info = await Owned($"{project}-raft-node-{id}", ct);
        var container = info["Id"]!.GetValue<string>();
        await Post($"/containers/{container}/{action.ToLowerInvariant()}?t=30", null, ct);
    }
    public async Task<bool> IsRunning(DockerActionIdentity identity, CancellationToken ct)
    {
        if (identity.Kind == "exec")
        {
            var info = await Get($"/exec/{Segment(identity.Id)}/json", ct, missing: true);
            // A removed/recreated target destroys exec evidence. It is not proof of completion.
            if (info is null) throw new ReplicationLabException(409, "Docker exec evidence is unavailable; inspection is required.");
            return info["Running"]!.GetValue<bool>();
        }
        if (identity.Kind == "maintenance")
        {
            var info = await Owned(identity.Id, ct, maintenance: true);
            return info["State"]!["Running"]!.GetValue<bool>();
        }
        throw new InvalidOperationException("Unknown Docker action identity.");
    }

    private async Task<string> Exec(string node, string[] command, CancellationToken ct)
    {
        PostgresReplicationCluster.CheckNode(node);
        var info = await Owned($"{project}-{node}", ct);
        var created = await Post($"/containers/{info["Id"]!.GetValue<string>()}/exec",
            new { Cmd = command, AttachStdout = false, AttachStderr = false }, ct);
        var id = created!["Id"]!.GetValue<string>();
        Record("exec", id); // Persist before starting any external effect.
        await Post($"/exec/{id}/start", new { Detach = true, Tty = false }, ct);
        while (true)
        {
            var status = await Get($"/exec/{id}/json", ct);
            if (!status!["Running"]!.GetValue<bool>())
            {
                if (status["ExitCode"]!.GetValue<int>() != 0) throw new ReplicationLabException(503, "Docker exec завершился ошибкой.");
                return "";
            }
            await Task.Delay(100, ct);
        }
    }
    private async Task<string> Wait(string name, CancellationToken ct)
    {
        var info = await Owned(name, ct, maintenance: true);
        var result = await Post($"/containers/{info["Id"]!.GetValue<string>()}/wait?condition=not-running", null, ct);
        return result!["StatusCode"]!.ToJsonString();
    }
    private async Task<string> MaintenanceImage(JsonNode original, CancellationToken ct)
    {
        var image = original["Image"]!.GetValue<string>();
        if (await Get($"/images/{Segment(image)}/json", ct, missing: true) is not null) return image;
        // With the containerd image store a rebuild can replace an index containing
        // identical platform layers but new provenance, removing the old index name.
        // Accept another local index only when it contains the exact original platform manifest.
        var digest = original["ImageManifestDescriptor"]?["digest"]?.GetValue<string>();
        if (digest is not null)
        {
            var images = await Get("/images/json?all=true&manifests=true", ct);
            foreach (var candidate in images!.AsArray())
                if (candidate?["Manifests"] is JsonArray manifests && manifests.Any(manifest =>
                    manifest?["ID"]?.GetValue<string>() == digest && manifest?["Available"]?.GetValue<bool>() == true &&
                    manifest?["Kind"]?.GetValue<string>() == "image"))
                    return candidate["Id"]!.GetValue<string>();
        }
        throw new ReplicationLabException(503, "Исходный образ PostgreSQL недоступен. Подмена непроверенным новым образом запрещена.");
    }
    private async Task<string> Maintenance(string[] args, CancellationToken ct)
    {
        var node = args[^2]; PostgresReplicationCluster.CheckNode(node);
        var script = args[^1];
        if (script is not ("/opt/replication/rejoin.sh" or "/opt/replication/restore-source.sh")) throw new ArgumentException("Invalid maintenance script.");
        var scope = ExecutionScope.Current ?? throw new InvalidOperationException("Maintenance requires an execution journal.");
        var name = $"{project}-raft-maintenance-{scope.OperationId:N}";
        var original = await Owned($"{project}-{node}", ct);
        var image = await MaintenanceImage(original, ct);
        var environment = original["Config"]!["Env"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "-e")
            {
                var entry = args[i + 1];
                if (!entry.StartsWith("SOURCE_NODE=", StringComparison.Ordinal)) throw new ArgumentException("Invalid maintenance environment.");
                PostgresReplicationCluster.CheckNode(entry["SOURCE_NODE=".Length..]);
                environment.RemoveAll(x => x.StartsWith("SOURCE_NODE=", StringComparison.Ordinal)); environment.Add(entry);
            }
        var networks = original["NetworkSettings"]!["Networks"]!.AsObject().Select(x => x.Key).ToArray();
        if (networks.Length != 1) throw new ReplicationLabException(409, "Для maintenance нужна одна подтверждённая сеть PostgreSQL.");
        var delay = Math.Clamp(configuration?.GetValue("Docker:MaintenanceDelayMs", 0) ?? 0, 0, 30000);
        var command = delay == 0 ? new[] { script } : new[] { "-c", $"sleep {(delay + 999) / 1000}; exec sh \"$1\"", "maintenance", script };
        var created = await Post($"/containers/create?name={Segment(name)}", new
        {
            Image = image, Env = environment, Entrypoint = new[] { "sh" }, Cmd = command,
            Labels = new Dictionary<string, string> { ["com.docker.compose.project"] = project, ["com.docker.compose.service"] = node,
                ["com.docker.compose.oneoff"] = "True", ["burmalda.raft.operation"] = scope.OperationId.ToString() },
            HostConfig = new { VolumesFrom = new[] { original["Id"]!.GetValue<string>() }, NetworkMode = networks[0] }
        }, ct);
        var id = created!["Id"]!.GetValue<string>(); Record("maintenance", id);
        await Post($"/containers/{id}/start", null, ct);
        if (await Wait(id, ct) != "0") throw new ReplicationLabException(503, "Maintenance завершился ошибкой; контейнер сохранён для проверки.");
        return "";
    }
    // Input comes only from the fixed command builders in PostgresReplicationCluster.
    public async Task<string> Run(CancellationToken ct, params string[] args)
    {
        if (args is ["compose", "exec", "-T", var node, ..]) return await Exec(node, args[4..], ct);
        if (args is ["compose", "stop", "--timeout", "30", var stopNode])
        {
            PostgresReplicationCluster.CheckNode(stopNode);
            var info = await Owned($"{project}-{stopNode}", ct);
            await Post($"/containers/{info["Id"]!.GetValue<string>()}/stop?t=30", null, ct); return "";
        }
        if (args.Length > 2 && args[0] == "compose" && args[1] == "run") return await Maintenance(args, ct);
        if (args is ["start", var startName])
        {
            var info = await Owned(startName, ct);
            await Post($"/containers/{info["Id"]!.GetValue<string>()}/start", null, ct); return "";
        }
        if (args is ["wait", var waitName]) return await Wait(waitName, ct);
        if (args is ["inspect", "--format", var format, var inspectName])
        {
            var maintenance = inspectName.StartsWith($"{project}-raft-maintenance-", StringComparison.Ordinal);
            var info = await Owned(inspectName, ct, maintenance);
            return format switch { "{{.State.Running}}" => info["State"]!["Running"]!.ToJsonString(),
                "{{.State.ExitCode}}" => info["State"]!["ExitCode"]!.ToJsonString(), _ => throw new ArgumentException("Invalid inspect format.") };
        }
        if (args.Length > 1 && args[0] == "inspect")
        {
            var result = new JsonArray();
            foreach (var id in args[1..]) result.Add(await Owned(id, ct, maintenance: true));
            return result.ToJsonString();
        }
        if (args is ["rm", var removeId])
        {
            var info = await Owned(removeId, ct, maintenance: true);
            if (info["State"]!["Status"]!.GetValue<string>() != "exited" || info["State"]!["ExitCode"]!.GetValue<int>() != 0)
                throw new ReplicationLabException(409, "Удаление незавершённого maintenance запрещено.");
            await Send(HttpMethod.Delete, $"/containers/{info["Id"]!.GetValue<string>()}?force=false&v=false", null, ct); return "";
        }
        if (args.Length > 0 && args[0] == "ps")
        {
            var nameFilter = Array.Find(args, x => x.StartsWith("name=", StringComparison.Ordinal));
            var filters = nameFilter is null
                ? new Dictionary<string, string[]> { ["label"] = [$"com.docker.compose.project={project}", "burmalda.raft.operation"] }
                : new Dictionary<string, string[]> { ["name"] = [nameFilter[5..]] };
            var list = await Get($"/containers/json?all=true&filters={Segment(JsonSerializer.Serialize(filters))}", ct);
            return string.Join('\n', list!.AsArray().Select(x => nameFilter is null ? x!["Id"]!.GetValue<string>() : x!["Names"]![0]!.GetValue<string>().TrimStart('/')));
        }
        throw new InvalidOperationException("Unsupported Docker lab command.");
    }
}
