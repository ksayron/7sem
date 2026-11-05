using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Burmalda.ConcurrencyLab.Api;

public sealed record ReplicationRequest(string ClientId);
public sealed record InstanceControl([property: JsonRequired] bool Available);
public sealed record ReplicationControl([property: JsonRequired] bool Enabled);
public sealed record CycleRequest(Guid CopyId);
public sealed record ReplicationEvent(long Sequence, DateTimeOffset AtUtc, string ClientId, string Operation,
    string Instance, string Result, string Detail);
public sealed record ReplicationRow(long Number, string ClientId, string Operation, Guid? CopyId,
    string? UsedInstance, bool? Committed, string Replication, string Result, string Message);
public sealed record ReplicationMilestone(string Label, string? Writer, IReadOnlyList<ReplicationNode> Nodes);
public sealed record ReplicationSnapshot(Guid Id, string? ActiveInstance, bool Consistent, long? ReplayLagBytes,
    bool ClusterBusy, string ActionStatus, string? ActionError, IReadOnlyList<ReplicationNode> Nodes,
    IReadOnlyList<ReplicationRow> Operations, IReadOnlyList<ReplicationEvent> Events, IReadOnlyList<ReplicationMilestone> Milestones);
public sealed class ReplicationSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ActionStatus { get; set; } = "Idle";
    public string? ActionError { get; set; }
    public List<ReplicationRow> Operations { get; set; } = [];
    public List<ReplicationEvent> Events { get; set; } = [];
    public List<ReplicationMilestone> Milestones { get; set; } = [];
}
public sealed class ReplicationAuthority
{
    public Guid? SessionId { get; set; }
    public string? FencedSource { get; set; }
    public string? RequiredLsn { get; set; }
    public bool Promoted { get; set; }
}

// One owner and one gate for the actual cluster. Sessions group evidence, never storage copies.
public sealed class ReplicationLabStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ConcurrentDictionary<Guid, ReplicationSession> sessions = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly IReplicationCluster cluster;
    private readonly ILogger logger;
    private readonly string root;
    private readonly FileStream ownership;
    private readonly CancellationToken stopping;
    private readonly TimeSpan catchupTimeout;
    private ReplicationAuthority authority;
    private volatile bool actionRunning;
    private ReplicationNode[] nodes = PostgresReplicationCluster.NodeIds.Select(id => new ReplicationNode(id,
        "UNKNOWN", "Unknown", true, false, null, null, false, null, null, [], [], "Ещё не проверен.")).ToArray();

    public ReplicationLabStore(string root, IReplicationCluster cluster, ILogger<ReplicationLabStore> logger,
        CancellationToken stopping = default, TimeSpan? catchupTimeout = null)
    {
        this.root = Path.GetFullPath(root); this.cluster = cluster; this.logger = logger; this.stopping = stopping;
        this.catchupTimeout = catchupTimeout ?? TimeSpan.FromSeconds(20);
        Directory.CreateDirectory(this.root);
        ownership = new FileStream(Path.Combine(this.root, "coordinator.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        authority = Load<ReplicationAuthority>("authority.json") ?? new();
        foreach (var file in Directory.EnumerateFiles(this.root, "session-*.json"))
        {
            var session = JsonSerializer.Deserialize<ReplicationSession>(File.ReadAllText(file), Json)
                ?? throw new InvalidDataException($"Invalid session journal: {file}");
            if (session.ActionStatus == "Running") { session.ActionStatus = "Interrupted"; session.ActionError = "API перезапущен; проверьте роли и восстановите остановленный узел."; }
            sessions[session.Id] = session;
        }
    }
    private T? Load<T>(string name) => File.Exists(Path.Combine(root, name))
        ? JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(root, name)), Json) : default;
    private void Save(string name, object data)
    {
        var path = Path.Combine(root, name); var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(data, Json)); File.Move(temp, path, true);
    }
    private void Save(ReplicationSession run) { lock (run) Save($"session-{run.Id}.json", run); }
    private void SaveAuthority() => Save("authority.json", authority);
    private ReplicationSession Get(Guid id) => sessions.TryGetValue(id, out var run) ? run
        : throw new ReplicationLabException(404, "Опыт не найден.");
    public async Task<ReplicationSnapshot> Create(CancellationToken ct = default)
    {
        if (sessions.Count >= 200) throw new ReplicationLabException(409, "Лимит 200 журналов опытов. Архивируйте завершённые журналы при остановленном API.");
        var run = new ReplicationSession(); sessions[run.Id] = run;
        Log(run, "operator", "Session", "cluster", "SUCCESS", "Журнал создан. Catalog и роли узлов не изменены.");
        return await Snapshot(run.Id, ct);
    }
    public async Task<ReplicationSnapshot> Snapshot(Guid id, CancellationToken ct = default)
    {
        var run = Get(id);
        if (await gate.WaitAsync(0, ct))
        {
            try { await Refresh(ct); } finally { gate.Release(); }
        }
        return Capture(run);
    }
    private async Task Refresh(CancellationToken ct)
    {
        var next = new List<ReplicationNode>();
        foreach (var id in PostgresReplicationCluster.NodeIds)
        {
            try { next.Add(await cluster.Observe(id, ct)); }
            catch (Exception ex) when (Expected(ex))
            {
                var previous = nodes.Single(n => n.Id == id);
                next.Add(previous with { State = "UNAVAILABLE", Role = "Unknown", ReadOnly = true,
                    Fenced = authority.FencedSource == id, Error = ex is ReplicationLabException ? ex.Message : "PostgreSQL/Catalog недоступен. Показаны последние наблюдавшиеся данные." });
            }
        }
        nodes = next.ToArray();
        if (nodes.Count(IsWriter) > 1) throw new ReplicationLabException(409, "Два writable primary: операции заблокированы. Требуется fencing.");
    }
    private static bool IsWriter(ReplicationNode n) => n.State == "AVAILABLE" && n.Role == "Primary" && !n.ReadOnly && !n.Fenced;
    private ReplicationNode? Writer => nodes.Count(IsWriter) == 1 ? nodes.First(IsWriter) : null;
    private ReplicationNode? Standby => nodes.FirstOrDefault(n => n.State == "AVAILABLE" && n.Role == "Standby" && !n.Fenced);
    public static ulong ParseLsn(string lsn)
    {
        var parts = lsn.Split('/');
        if (parts.Length != 2) throw new FormatException("Invalid PostgreSQL LSN.");
        return ((ulong)uint.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture) << 32)
            | uint.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
    public static bool Replayed(string? replay, string? required) => replay is not null && required is not null && ParseLsn(replay) >= ParseLsn(required);
    private ReplicationSnapshot Capture(ReplicationSession run)
    {
        var writer = Writer; var standby = Standby;
        var lag = writer?.WalLsn is { } w && standby?.ReplayLsn is { } r ? (long?)Math.Max(0, (long)ParseLsn(w) - (long)ParseLsn(r)) : null;
        var equal = writer is not null && standby is not null && lag == 0 &&
            writer.Books.SequenceEqual(standby.Books) && writer.Copies.SequenceEqual(standby.Copies);
        lock (run) return new(run.Id, writer?.Id, equal, lag, gate.CurrentCount == 0, run.ActionStatus, run.ActionError,
            nodes, run.Operations.ToArray(), run.Events.ToArray(), run.Milestones.ToArray());
    }
    private void Log(ReplicationSession run, string client, string operation, string node, string result, string message)
    {
        lock (run)
        {
            run.Events.Add(new(run.Events.LastOrDefault()?.Sequence + 1 ?? 1, DateTimeOffset.UtcNow, client, operation, node, result, message));
            if (run.Events.Count > 2000) run.Events.RemoveAt(0);
            Save(run);
        }
        logger.LogInformation("LR5 {Run} {Client} {Operation} {Node} {Result}: {Message}", run.Id, client, operation, node, result, message);
    }
    private void Row(ReplicationSession run, string client, string operation, Guid? copy, string? used, bool? committed,
        string replication, string result, string message)
    {
        lock (run)
        {
            run.Operations.Add(new(run.Operations.LastOrDefault()?.Number + 1 ?? 1, client, operation, copy, used, committed, replication, result, message));
            if (run.Operations.Count > 1000) run.Operations.RemoveAt(0);
        }
        Log(run, client, operation, used ?? "cluster", result, message);
    }
    private void Mark(ReplicationSession run, string label)
    { lock (run) run.Milestones.Add(new(label, Writer?.Id, nodes)); Save(run); }
    private static bool Expected(Exception ex) => ex is ReplicationLabException or System.Data.Common.DbException
        or IOException or TimeoutException or OperationCanceledException;
    private static void Client(string client)
    { if (string.IsNullOrWhiteSpace(client) || client.Length > 32) throw new ReplicationLabException(400, "ClientId: 1–32 символа."); }
    public async Task<ReplicationSnapshot> Read(Guid id, ReplicationRequest input, CancellationToken ct)
    {
        Client(input.ClientId); var run = Get(id);
        if (actionRunning || !await gate.WaitAsync(TimeSpan.FromSeconds(5), ct)) throw new ReplicationLabException(409, "Кластер занят. Дождитесь завершения операции.");
        try
        {
            await Refresh(ct); var writer = await EnsureWriter(run, ct);
            Row(run, input.ClientId, "Read", null, writer.Id, false, "NOT_REQUIRED", "SUCCESS", "Catalog прочитан из PostgreSQL.");
        }
        catch (Exception ex) when (Expected(ex)) { Row(run, input.ClientId, "Read", null, null, false, "NOT_REQUIRED", "FAIL", ex.Message); throw; }
        finally { gate.Release(); }
        return Capture(run);
    }
    public async Task<ReplicationSnapshot> Update(Guid id, CopyAvailabilityRequest input, CancellationToken ct)
    {
        Validate(input); var run = Get(id);
        if (actionRunning || !await gate.WaitAsync(TimeSpan.FromSeconds(5), ct)) throw new ReplicationLabException(409, "Кластер занят. Дождитесь завершения операции.");
        try { await Change(run, input, stopping); }
        finally { gate.Release(); }
        return Capture(run);
    }
    public static void Validate(CopyAvailabilityRequest input)
    {
        Client(input.ClientId);
        if (input.CopyId == Guid.Empty || input.ExpectedVersion < 0) throw new ReplicationLabException(400, "Нужны CopyId и ExpectedVersion из Catalog.");
    }
    private async Task Change(ReplicationSession run, CopyAvailabilityRequest input, CancellationToken ct)
    {
        string? used = null; bool? committed = false;
        try
        {
            await Refresh(ct); used = (await EnsureWriter(run, ct)).Id;
            var result = await cluster.Update(used, input, ct); committed = true;
            await Refresh(ct);
            var replicated = Standby is { } standby && Replayed(standby.ReplayLsn, result.WalLsn);
            Row(run, input.ClientId, "Update", input.CopyId, used, true, replicated ? "REPLAYED" : "PENDING", "SUCCESS",
                $"COMMIT: Copy {input.CopyId}; version {result.Version}; WAL {result.WalLsn}. Standby: {(replicated ? "replayed" : "pending/unavailable")}.");
        }
        catch (Exception ex) when (Expected(ex))
        {
            if (ex.Message.Contains("COMMIT неизвестен", StringComparison.Ordinal)) committed = null;
            Row(run, input.ClientId, "Update", input.CopyId, used, committed, "UNKNOWN", "FAIL", ex.Message); throw;
        }
    }
    private async Task<ReplicationNode> EnsureWriter(ReplicationSession run, CancellationToken ct, bool allowPromotion = false)
    {
        if (Writer is { } writer) return writer;
        if (!allowPromotion || cluster is RaftManagedReplicationCluster)
            throw new ReplicationLabException(503, "Writable primary недоступен. Чтение не запускает promotion; состояние переключения смотрите в Raft.");
        if (authority.FencedSource is null || authority.RequiredLsn is null || authority.Promoted)
            throw new ReplicationLabException(503, "Writable primary недоступен. Без подтверждённого WAL/fencing promotion запрещён; восстановите исходный узел.");
        if (!await cluster.IsStopped(authority.FencedSource, ct)) throw new ReplicationLabException(409, "Fencing не подтверждён: прежний primary ещё работает.");
        var target = Standby ?? throw new ReplicationLabException(503, "Доступного standby нет.");
        if (target.ReplayPaused || !Replayed(target.ReplayLsn, authority.RequiredLsn))
            throw new ReplicationLabException(409, "Standby отстаёт или replay приостановлен. Promotion запрещён; восстановите исходный primary.");
        await cluster.Promote(target.Id, ct); authority.Promoted = true; SaveAuthority(); await Refresh(ct);
        Log(run, "operator", "Failover", target.Id, "SUCCESS", $"Реальный pg_promote; прежний primary {authority.FencedSource} остановлен и fenced.");
        return Writer ?? throw new ReplicationLabException(503, "Promotion отправлен; writable роль пока не подтверждена. Обновите состояние.");
    }
    // The same exclusive gate protects operator actions and automatic failover.
    // A lost TCP connection alone is never permission to promote an async standby.
    public async Task MaintainWriter(CancellationToken ct)
    {
        if (cluster is RaftManagedReplicationCluster) return;
        if (authority.FencedSource is null || authority.RequiredLsn is null || authority.Promoted) return;
        if (actionRunning || !await gate.WaitAsync(0, ct)) return;
        try
        {
            if (authority.FencedSource is null || authority.RequiredLsn is null || authority.Promoted) return;
            await Refresh(ct);
            if (Writer is { } writer)
            {
                // Reconcile a crash after pg_promote but before authority.json was saved.
                if (authority.FencedSource is { } old && old != writer.Id && !authority.Promoted)
                { authority.Promoted = true; SaveAuthority(); }
                return;
            }
            if (authority.FencedSource is null || authority.RequiredLsn is null || authority.Promoted) return;
            var run = authority.SessionId is { } id && sessions.TryGetValue(id, out var owner) ? owner
                : sessions.Values.OrderByDescending(s => s.Events.LastOrDefault()?.AtUtc)
                    .FirstOrDefault(s => s.Events.Any(e => e.Operation == "Stop" && e.Instance == authority.FencedSource));
            if (run is null)
            {
                run = new ReplicationSession(); sessions[run.Id] = run;
                authority.SessionId = run.Id; SaveAuthority();
            }
            await EnsureWriter(run, ct, allowPromotion: true);
            Mark(run, "Автоматический failover: standby повышен до primary");
        }
        finally { gate.Release(); }
    }
    public async Task<ReplicationSnapshot> Configure(Guid id, ReplicationControl input, CancellationToken ct)
    {
        var run = Get(id); if (actionRunning || !await gate.WaitAsync(TimeSpan.FromSeconds(5), ct)) throw new ReplicationLabException(409, "Кластер занят.");
        try
        {
            await Refresh(ct); var standby = Standby ?? throw new ReplicationLabException(409, "Нет доступного standby.");
            await cluster.Pause(standby.Id, !input.Enabled, ct); await Refresh(ct);
            Log(run, "operator", "Replay", standby.Id, "SUCCESS", input.Enabled ? "WAL replay возобновлён." : "WAL replay приостановлен; WAL transport продолжает работать.");
        }
        finally { gate.Release(); }
        return Capture(run);
    }
    public ReplicationSnapshot StartAction(Guid id, string action, string? node = null, Guid? copy = null)
    {
        var run = Get(id); if (node is not null) PostgresReplicationCluster.CheckNode(node);
        if (actionRunning || !gate.Wait(TimeSpan.FromSeconds(15))) throw new ReplicationLabException(409, "Кластер занят другим действием/циклом.");
        actionRunning = true;
        lock (run) { run.ActionStatus = "Running"; run.ActionError = null; }
        try { Save(run); } catch { actionRunning = false; gate.Release(); throw; }
        _ = Task.Run(async () =>
        {
            try
            {
                await Refresh(stopping);
                switch (action)
                {
                    case "stop": await StopNode(run, node!, stopping); break;
                    case "recover": await RecoverNode(run, node!, stopping); break;
                    case "sync": await CatchUp(run, stopping); break;
                    case "cycle": await Cycle(run, copy ?? Guid.Empty, stopping); break;
                    default: throw new ReplicationLabException(400, "Неизвестное действие.");
                }
                lock (run) run.ActionStatus = "Completed";
            }
            catch (Exception ex)
            {
                lock (run) { run.ActionStatus = "Failed"; run.ActionError = ex is ReplicationLabException ? ex.Message : "Операция прервана/не выполнена. Проверьте состояние Docker и PostgreSQL; повторного изменения данных нет."; }
                Log(run, "operator", action, node ?? "cluster", "FAIL", run.ActionError);
            }
            finally { try { Save(run); } finally { actionRunning = false; gate.Release(); } }
        });
        return Capture(run);
    }
    private async Task StopNode(ReplicationSession run, string node, CancellationToken ct)
    {
        if (Writer?.Id == node)
        {
            if (authority.FencedSource is not null && authority.Promoted)
                throw new ReplicationLabException(409, "Сначала восстановите прежний primary как standby.");
            authority.SessionId = run.Id; authority.FencedSource = node; authority.Promoted = false; authority.RequiredLsn = null; SaveAuthority();
            authority.RequiredLsn = await cluster.QuiesceAndFence(node, ct); SaveAuthority();
        }
        await cluster.Stop(node, ct);
        if (!await cluster.IsStopped(node, ct)) throw new ReplicationLabException(409, "Контейнер не остановлен.");
        await Refresh(ct); Mark(run, $"Контейнер {node} остановлен");
        Log(run, "operator", "Stop", node, "SUCCESS", $"Реальный Docker stop подтверждён. Fenced source: {authority.FencedSource ?? "none"}; required WAL: {authority.RequiredLsn ?? "none"}.");
    }
    private async Task RecoverNode(ReplicationSession run, string node, CancellationToken ct)
    {
        if (Writer?.Id == node) throw new ReplicationLabException(409, "Узел уже является writable primary.");
        if (authority.FencedSource == node)
        {
            // Observe actual roles too: promotion may have completed just before an API crash.
            if (Writer is { } writer)
            { authority.Promoted = true; SaveAuthority(); await cluster.Recover(node, writer.Id, ct); }
            else if (!authority.Promoted) await cluster.RestoreSource(node, ct);
            else throw new ReplicationLabException(503, "Новый primary недоступен. Восстановите его до pg_rewind старого узла.");
        }
        else await cluster.Start(node, ct);
        await WaitFor(async () => { await Refresh(ct); return nodes.Single(n => n.Id == node).State == "AVAILABLE"; }, ct);
        if (Writer is not null && Standby is not null) await CatchUp(run, ct);
        if (authority.FencedSource == node) { authority = new(); SaveAuthority(); }
        Log(run, "operator", "Recovery", node, "SUCCESS", "Узел восстановлен; фактические роли проверены. Принудительного failback нет.");
        Mark(run, "Восстановление и streaming replication");
    }
    private async Task CatchUp(ReplicationSession run, CancellationToken ct)
    {
        await Refresh(ct); var writer = Writer ?? throw new ReplicationLabException(503, "Primary недоступен.");
        var standby = Standby ?? throw new ReplicationLabException(503, "Standby недоступен; восстановите его.");
        await cluster.Pause(standby.Id, false, ct);
        var boundary = writer.WalLsn;
        await WaitFor(async () => { await Refresh(ct); return Standby is { } s && Replayed(s.ReplayLsn, boundary); }, ct);
        Log(run, "operator", "CatchUp", standby.Id, "SUCCESS", $"Standby воспроизвёл WAL до {boundary}.");
    }
    private async Task WaitFor(Func<Task<bool>> ready, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + catchupTimeout;
        do { if (await ready()) return; await Task.Delay(300, ct); } while (DateTimeOffset.UtcNow < deadline);
        throw new ReplicationLabException(503, "Истёк срок ожидания PostgreSQL/WAL replay. Проверьте состояние и повторите восстановление.");
    }
    private async Task Cycle(ReplicationSession run, Guid copyId, CancellationToken ct)
    {
        var writer = await EnsureWriter(run, ct);
        var original = writer.Copies.SingleOrDefault(c => c.Id == copyId && c.Status is "Available" or "Unavailable")
            ?? throw new ReplicationLabException(409, "Выберите существующий Available/Unavailable экземпляр Catalog. Учебные книги автоматически не создаются.");
        if (Standby is null) throw new ReplicationLabException(409, "Полный цикл требует доступный standby.");
        lock (run) run.Milestones.Clear();
        Mark(run, "Начальные данные Catalog");
        long expected = original.Version;
        async Task Toggle(string client, bool available)
        {
            await Change(run, new(client, copyId, available, expected), ct); expected++;
            Mark(run, $"{client}: Available={available}");
        }
        var baseline = original.Status == "Available";
        await Toggle("Client 1", !baseline); await CatchUp(run, ct);
        await Toggle("Client 2", baseline); await CatchUp(run, ct);
        await Toggle("Client 1", !baseline); await CatchUp(run, ct);
        var old = writer.Id; await StopNode(run, old, ct);
        if (cluster is RaftManagedReplicationCluster)
            await WaitFor(async () => { await Refresh(ct); return Writer is not null; }, ct);
        await EnsureWriter(run, ct, allowPromotion: true); Mark(run, "Failover: standby повышен до primary");
        Row(run, "Client 1", "Read", copyId, Writer!.Id, false, "NOT_REQUIRED", "SUCCESS", "Чтение после promotion.");
        await Toggle("Client 2", baseline);
        await RecoverNode(run, old, ct); await CatchUp(run, ct);
        await Toggle("Client 1", !baseline); await CatchUp(run, ct);
        await Toggle("Client 2", baseline); await CatchUp(run, ct);
        Mark(run, "Цикл завершён; исходная доступность восстановлена");
    }
    public void Dispose() => ownership.Dispose();
}

public static class ReplicationLabEndpoints
{
    public static void MapReplicationLab(this WebApplication app)
    {
        var group = app.MapGroup("/api/concurrency-lab/replication/runs");
        group.MapPost("", (ReplicationLabStore s, CancellationToken ct) => s.Create(ct));
        group.MapGet("/{id:guid}", (Guid id, ReplicationLabStore s, CancellationToken ct) => s.Snapshot(id, ct));
        group.MapPost("/{id:guid}/read", (Guid id, ReplicationRequest input, ReplicationLabStore s, CancellationToken ct) => s.Read(id, input, ct));
        group.MapPost("/{id:guid}/update", (Guid id, CopyAvailabilityRequest input, ReplicationLabStore s, CancellationToken ct) => s.Update(id, input, ct));
        group.MapPost("/{id:guid}/instances/{node}", (Guid id, string node, InstanceControl input, ReplicationLabStore s) =>
            Results.Json(s.StartAction(id, input.Available ? "recover" : "stop", node), statusCode: 202));
        group.MapPost("/{id:guid}/replication", (Guid id, ReplicationControl input, ReplicationLabStore s, CancellationToken ct) => s.Configure(id, input, ct));
        group.MapPost("/{id:guid}/synchronize", (Guid id, ReplicationLabStore s) => Results.Json(s.StartAction(id, "sync"), statusCode: 202));
        group.MapPost("/{id:guid}/cycle", (Guid id, CycleRequest input, ReplicationLabStore s) => Results.Json(s.StartAction(id, "cycle", copy: input.CopyId), statusCode: 202));
    }
}
