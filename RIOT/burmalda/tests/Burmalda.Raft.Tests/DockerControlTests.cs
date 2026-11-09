using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using Burmalda.ClusterControl;
using Burmalda.ConcurrencyLab.Api;
using Burmalda.PostgresControl;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Burmalda.Raft.Tests;

public sealed class DockerControlTests
{
    private sealed class Handler(Func<HttpRequestMessage, Task<string>> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => new(HttpStatusCode.OK) { Content = new StringContent(await respond(request), Encoding.UTF8, "application/json") };
    }
    private sealed class StatusHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }
    private static DockerEngineClient Engine(Func<HttpRequestMessage, Task<string>> respond)
        => new(new HttpClient(new Handler(respond)) { BaseAddress = new("http://docker") });
    private static string Container(string service, string project = "burmalda") => JsonSerializer.Serialize(new
    { Id = "db-id", Name = "/burmalda-" + service, Config = new { Labels = new Dictionary<string, string>
        { ["com.docker.compose.project"] = project, ["com.docker.compose.service"] = service } } });

    [Fact] public async Task Fresh_cluster_guard_preserves_and_blocks_unknown_legacy_effect()
    {
        var root = Path.Combine(Path.GetTempPath(), "legacy-guard-" + Guid.NewGuid());
        try
        {
            var journal = new ExecutionJournal(Path.Combine(root, "raft-runtime", "execution"));
            var operation = Guid.NewGuid();
            await Assert.ThrowsAsync<NeedsInspectionException>(() => journal.Run(operation, "Promote",
                () => throw new IOException("Unknown result"), () => Task.FromResult<string?>(null)));
            Assert.Throws<ReplicationLabException>(() => LegacyControlGuard.Check(root));
            Assert.True(journal.HasUnresolved(operation));
            await journal.Run(operation, "Promote", () => throw new Exception("Must not repeat"), () => Task.FromResult<string?>("proven"));
            LegacyControlGuard.Check(root);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact] public void Fresh_cluster_guard_requires_previous_host_nodes_to_be_stopped()
    {
        var root = Path.Combine(Path.GetTempPath(), "legacy-manifest-" + Guid.NewGuid());
        try
        {
            var directory = Path.Combine(root, "raft-runtime"); Directory.CreateDirectory(directory);
            var manifest = Path.Combine(directory, "processes.json"); File.WriteAllText(manifest, "[{\"NodeId\":1}]");
            Assert.Throws<ReplicationLabException>(() => LegacyControlGuard.Check(root));
            Assert.Equal("[{\"NodeId\":1}]", File.ReadAllText(manifest));
            File.WriteAllText(manifest, "[]"); LegacyControlGuard.Check(root);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact] public void Members_are_shared_and_invalid_membership_is_rejected()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(Enumerable.Range(1, 5)
            .Select(id => KeyValuePair.Create<string, string?>($"Raft:Members:{id}", $"http://raft-node-{id}:8080"))).Build();
        Assert.Equal("http://raft-node-5:8080", new RaftControlClient(config).Members[5]);
        config["Raft:Members:6"] = "http://extra:8080";
        Assert.Throws<InvalidOperationException>(() => RaftConfiguration.Members(config));
    }
    [Theory] [InlineData("other-project", "raft-node-1")] [InlineData("burmalda", "catalog")]
    public async Task Node_management_refuses_foreign_or_unexpected_container(string project, string service)
    {
        var mutations = 0;
        var engine = Engine(request => { if (request.Method != HttpMethod.Get) mutations++; return Task.FromResult(Container(service, project)); });
        await Assert.ThrowsAsync<ReplicationLabException>(() => engine.ManageRaft(1, "Stop", default));
        Assert.Equal(0, mutations);
    }
    [Fact] public async Task Exec_identity_is_persisted_before_daemon_start()
    {
        var recorded = new List<DockerActionIdentity>(); var started = false;
        var engine = Engine(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/start", StringComparison.Ordinal))
            { Assert.Equal(new DockerActionIdentity("exec", "exec-1"), Assert.Single(recorded)); started = true; return Task.FromResult(""); }
            return Task.FromResult(path switch { "/containers/burmalda-postgres/json" => Container("postgres"),
                "/containers/db-id/exec" => "{\"Id\":\"exec-1\"}", "/exec/exec-1/json" => "{\"Running\":false,\"ExitCode\":0}", _ => throw new Exception(path) });
        });
        using var scope = ExecutionScope.Enter(Guid.NewGuid(), _ => throw new Exception("Host PID recorded"), recorded.Add);
        await engine.Run(default, "compose", "exec", "-T", "postgres", "sh", "-c", "touch /control/$NODE_ID.fenced");
        Assert.True(started);
    }
    [Fact] public async Task Surviving_daemon_exec_blocks_another_leaders_effect()
    {
        var root = Path.Combine(Path.GetTempPath(), "docker-journal-" + Guid.NewGuid());
        var engine = Engine(_ => Task.FromResult("{\"Running\":true}"));
        try
        {
            var id = Guid.NewGuid(); var calls = 0;
            var journal = new ExecutionJournal(root, engine);
            await Assert.ThrowsAsync<NeedsInspectionException>(() => journal.Run(id, "Fence", () =>
            {
                calls++; ExecutionScope.Current!.DockerStarted(new("exec", "still-alive")); throw new IOException("Leader exited");
            }, () => Task.FromResult<string?>(null)));
            var nextLeader = new ExecutionJournal(root, engine);
            await Assert.ThrowsAsync<ExternalActionRunningException>(() => nextLeader.Run(id, "Fence", () =>
            { calls++; return Task.FromResult("wrong"); }, () => Task.FromResult<string?>("done")));
            Assert.Equal(1, calls);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public async Task Maintenance_uses_original_image_volumes_and_network_and_records_before_start()
    {
        var operation = Guid.NewGuid(); var recorded = new List<DockerActionIdentity>(); var created = false;
        var engine = Engine(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/containers/burmalda-postgres/json")
            {
                var info = JsonNode.Parse(Container("postgres"))!;
                info["Image"] = "sha256:database-image"; info["Config"]!["Env"] = new JsonArray("NODE_ID=postgres");
                info["NetworkSettings"] = JsonNode.Parse("{\"Networks\":{\"burmalda\":{}}}"); return info.ToJsonString();
            }
            if (path == "/containers/create")
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
                Assert.Equal("sha256:database-image", body["image"]!.GetValue<string>());
                Assert.Equal("db-id", body["hostConfig"]!["volumesFrom"]![0]!.GetValue<string>());
                Assert.Equal("burmalda", body["hostConfig"]!["networkMode"]!.GetValue<string>());
                created = true; return "{\"Id\":\"job-id\"}";
            }
            if (path.StartsWith("/images/", StringComparison.Ordinal)) return "{\"Id\":\"sha256:database-image\"}";
            if (path == "/containers/job-id/start")
            { Assert.Equal(new DockerActionIdentity("maintenance", "job-id"), Assert.Single(recorded)); return ""; }
            if (path == "/containers/job-id/json") return JsonSerializer.Serialize(new
            { Id = "job-id", Name = $"/burmalda-raft-maintenance-{operation:N}", Config = new { Labels = new Dictionary<string, string>
                { ["com.docker.compose.project"] = "burmalda", ["com.docker.compose.service"] = "postgres",
                  ["com.docker.compose.oneoff"] = "True", ["burmalda.raft.operation"] = operation.ToString() } } });
            if (path == "/containers/job-id/wait") return "{\"StatusCode\":0}";
            throw new Exception(path);
        });
        using var scope = ExecutionScope.Enter(operation, _ => throw new Exception("Host PID recorded"), recorded.Add);
        await engine.Run(default, "compose", "run", "--no-deps", "--name", $"burmalda-raft-maintenance-{operation:N}",
            "-e", "SOURCE_NODE=postgres-replica", "--entrypoint", "sh", "postgres", "/opt/replication/rejoin.sh");
        Assert.True(created);
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task Replaced_image_index_requires_the_exact_original_platform_manifest(bool matches)
    {
        var operation = Guid.NewGuid(); var created = false;
        static HttpResponseMessage Reply(string text, HttpStatusCode code = HttpStatusCode.OK)
            => new(code) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        using var http = new HttpClient(new StatusHandler(async request =>
        {
            var route = request.RequestUri!.AbsolutePath;
            if (route == "/containers/burmalda-postgres/json")
            {
                var node = JsonNode.Parse(Container("postgres"))!; node["Image"] = "old-index";
                node["ImageManifestDescriptor"] = JsonNode.Parse("{\"digest\":\"original-manifest\"}");
                node["Config"]!["Env"] = new JsonArray("NODE_ID=postgres");
                node["NetworkSettings"] = JsonNode.Parse("{\"Networks\":{\"burmalda\":{}}}"); return Reply(node.ToJsonString());
            }
            if (route == "/images/old-index/json") return Reply("{}", HttpStatusCode.NotFound);
            if (route == "/images/json") return Reply(JsonSerializer.Serialize(new[] { new
            { Id = "verified-index", Manifests = new[] { new { ID = matches ? "original-manifest" : "other-manifest", Available = true, Kind = "image" } } } }));
            if (route == "/containers/create")
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
                Assert.Equal("verified-index", body["image"]!.GetValue<string>()); created = true; return Reply("{\"Id\":\"job-id\"}");
            }
            if (route == "/containers/job-id/start") return Reply("");
            if (route == "/containers/job-id/json") return Reply(JsonSerializer.Serialize(new
            { Id = "job-id", Name = $"/burmalda-raft-maintenance-{operation:N}", Config = new { Labels = new Dictionary<string, string>
                { ["com.docker.compose.project"] = "burmalda", ["com.docker.compose.service"] = "postgres",
                  ["com.docker.compose.oneoff"] = "True", ["burmalda.raft.operation"] = operation.ToString() } } }));
            if (route == "/containers/job-id/wait") return Reply("{\"StatusCode\":0}");
            throw new Exception(route);
        })) { BaseAddress = new("http://docker") };
        var engine = new DockerEngineClient(http);
        using var scope = ExecutionScope.Enter(operation, _ => throw new Exception("Host PID recorded"), _ => { });
        Task Run() => engine.Run(default, "compose", "run", "--name", "job", "postgres", "/opt/replication/restore-source.sh");
        if (matches) await Run(); else await Assert.ThrowsAsync<ReplicationLabException>(Run);
        Assert.Equal(matches, created);
    }
}
