using System.Diagnostics;
using Npgsql;
using System.Text.Json.Serialization;

namespace Burmalda.ConcurrencyLab.Api;

public sealed record ReplicationBook(Guid Id, string Title, int Available);
public sealed record ReplicationCopy(Guid Id, Guid BookId, string Title, string Status, bool IsHidden, long Version);
public sealed record ReplicationNode(string Id, string State, string Role, bool ReadOnly, bool Fenced,
    string? WalLsn, string? ReplayLsn, bool ReplayPaused, string? StreamingState, DateTimeOffset? ObservedAtUtc,
    IReadOnlyList<ReplicationBook> Books, IReadOnlyList<ReplicationCopy> Copies, string? Error);
public sealed record CopyAvailabilityRequest(string ClientId, Guid CopyId,
    [property: JsonRequired] bool Available, [property: JsonRequired] long ExpectedVersion);
public sealed record CopyCommit(Guid CopyId, Guid BookId, long Version, string? WalLsn);

public interface IReplicationCluster
{
    Task<ReplicationNode> Observe(string node, CancellationToken ct);
    Task<CopyCommit> Update(string node, CopyAvailabilityRequest input, CancellationToken ct);
    Task<string> QuiesceAndFence(string node, CancellationToken ct);
    Task Stop(string node, CancellationToken ct);
    Task<bool> IsStopped(string node, CancellationToken ct);
    Task Promote(string node, CancellationToken ct);
    Task Recover(string node, string source, CancellationToken ct);
    Task RestoreSource(string node, CancellationToken ct);
    Task Start(string node, CancellationToken ct);
    Task Pause(string node, bool paused, CancellationToken ct);
}

// All command arguments are constructed here, never from request-supplied shell text.
public sealed class PostgresReplicationCluster(IConfiguration config) : IReplicationCluster
{
    public static readonly string[] NodeIds = ["postgres", "postgres-replica"];
    private readonly string root = ResolveRoot(config["ReplicationLab:ComposeRoot"]);
    private string Connection(string node, bool catalog = false)
    {
        CheckNode(node);
        var c = new NpgsqlConnectionStringBuilder(config[$"ReplicationLab:Nodes:{node}"] ??
            $"Host=127.0.0.1;Port={(node == "postgres" ? 5547 : 5548)};Database=postgres;Username=burmalda_admin;Password=burmalda_admin_dev")
        { Timeout = 3, CommandTimeout = 15, Pooling = false, IncludeErrorDetail = false };
        if (catalog)
        {
            c.Database = "catalog_db";
            c.Username = config["ReplicationLab:CatalogUser"] ?? "catalog_app";
            c.Password = config["ReplicationLab:CatalogPassword"] ?? "catalog_dev";
        }
        return c.ConnectionString;
    }
    public static void CheckNode(string node)
    {
        if (!NodeIds.Contains(node)) throw new ReplicationLabException(400, "Узел: postgres или postgres-replica.");
    }
    public static string ResolveRoot(string? configured)
    {
        if (configured is not null) return Path.GetFullPath(configured);
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docker-compose.yml"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("ReplicationLab:ComposeRoot is required.");
    }
    private async Task<string> Docker(CancellationToken ct, params string[] args)
    {
        var start = new ProcessStartInfo("docker") { WorkingDirectory = root, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new ReplicationLabException(503, "Docker CLI недоступен.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        var result = await output; var stderr = await error;
        if (process.ExitCode != 0)
            throw new ReplicationLabException(503, $"Docker: операция не выполнена (exit {process.ExitCode}). {Redact(stderr)}");
        return result.Trim();
    }
    private string Redact(string text)
    {
        foreach (var key in new[] { "postgres", "postgres-replica" })
        {
            var password = new NpgsqlConnectionStringBuilder(Connection(key)).Password;
            if (!string.IsNullOrEmpty(password)) text = text.Replace(password, "***", StringComparison.Ordinal);
        }
        return text.Length > 1200 ? text[..1200] : text;
    }
    private Task<string> Exec(string node, CancellationToken ct, params string[] args)
    {
        CheckNode(node);
        return Docker(ct, ["compose", "exec", "-T", node, .. args]);
    }
    private async Task<object?> Scalar(string node, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(Connection(node)); await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection); return await command.ExecuteScalarAsync(ct);
    }
    public async Task<ReplicationNode> Observe(string node, CancellationToken ct)
    {
        CheckNode(node);
        await using var connection = new NpgsqlConnection(Connection(node)); await connection.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT pg_is_in_recovery(), current_setting('default_transaction_read_only')::boolean,
              CASE WHEN pg_is_in_recovery() THEN NULL ELSE pg_current_wal_flush_lsn()::text END,
              pg_last_wal_replay_lsn()::text,
              CASE WHEN pg_is_in_recovery() THEN pg_is_wal_replay_paused() ELSE false END,
              CASE WHEN pg_is_in_recovery() THEN (SELECT status FROM pg_stat_wal_receiver LIMIT 1)
                   ELSE (SELECT state FROM pg_stat_replication LIMIT 1) END,
              (pg_stat_file('/control/' || @node || '.fenced', true)).size IS NOT NULL
            """, connection);
        cmd.Parameters.AddWithValue("node", node);
        bool standby, readOnly, paused, fenced; string? wal, replay, streaming;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct); standby = reader.GetBoolean(0); readOnly = reader.GetBoolean(1);
            wal = reader.IsDBNull(2) ? null : reader.GetString(2); replay = reader.IsDBNull(3) ? null : reader.GetString(3);
            paused = reader.GetBoolean(4); streaming = reader.IsDBNull(5) ? null : reader.GetString(5); fenced = reader.GetBoolean(6);
        }
        var books = new List<ReplicationBook>(); var copies = new List<ReplicationCopy>();
        await using var catalog = new NpgsqlConnection(Connection(node, true)); await catalog.OpenAsync(ct);
        await using (var schema = new NpgsqlCommand("SELECT to_regclass('public.books') IS NOT NULL AND to_regclass('public.book_copies') IS NOT NULL", catalog))
            if (!Equals(await schema.ExecuteScalarAsync(ct), true))
                return new(node, "AVAILABLE", standby ? "Standby" : "Primary", readOnly, fenced, wal, replay, paused, streaming,
                    DateTimeOffset.UtcNow, [], [], "Схема Catalog пока не создана. Запустите -Full и добавьте книгу/экземпляр через приложение.");
        await using var transaction = await catalog.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await using (var query = new NpgsqlCommand("""
            SELECT b."Id", b."Title", count(c."Id") FILTER (WHERE c."Status" = 0 AND NOT c."IsHidden")::int
            FROM books b LEFT JOIN book_copies c ON c."BookId" = b."Id" GROUP BY b."Id", b."Title" ORDER BY b."Title", b."Id"
            """, catalog, transaction))
        await using (var reader = await query.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) books.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2)));
        await using (var query = new NpgsqlCommand("""
            SELECT c."Id", c."BookId", b."Title", c."Status", c."IsHidden", c."Version"
            FROM book_copies c JOIN books b ON c."BookId" = b."Id" ORDER BY b."Title", c."Id"
            """, catalog, transaction))
        await using (var reader = await query.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) copies.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
                reader.GetInt32(3) switch { 0 => "Available", 1 => "Reserved", 2 => "OnLoan", _ => "Unavailable" }, reader.GetBoolean(4), reader.GetInt64(5)));
        await transaction.CommitAsync(ct);
        return new(node, "AVAILABLE", standby ? "Standby" : "Primary", readOnly, fenced, wal, replay, paused,
            streaming, DateTimeOffset.UtcNow, books, copies, null);
    }
    public async Task<CopyCommit> Update(string node, CopyAvailabilityRequest input, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(Connection(node, true)); await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        Guid book; long version;
        // Lock the book as well as its copy: concurrent copy changes must serialize projection refreshes.
        await using (var lockBook = new NpgsqlCommand("""
            SELECT b."Id" FROM books b JOIN book_copies c ON c."BookId" = b."Id" WHERE c."Id" = @id FOR UPDATE OF b
            """, connection, tx))
        {
            lockBook.Parameters.AddWithValue("id", input.CopyId);
            book = (Guid?)await lockBook.ExecuteScalarAsync(ct) ?? throw new ReplicationLabException(404, "Экземпляр не найден в Catalog.");
        }
        await using (var update = new NpgsqlCommand("""
            UPDATE book_copies SET "Status" = @status, "Version" = "Version" + 1
            WHERE "Id" = @id AND "Version" = @version AND "Status" IN (0,3)
              AND "ReservationSagaId" IS NULL AND "ReservedForUserId" IS NULL AND "ReservationExpiresAt" IS NULL
            RETURNING "Version"
            """, connection, tx))
        {
            update.Parameters.AddWithValue("id", input.CopyId); update.Parameters.AddWithValue("status", input.Available ? 0 : 3);
            update.Parameters.AddWithValue("version", input.ExpectedVersion);
            version = (long?)await update.ExecuteScalarAsync(ct) ?? throw new ReplicationLabException(409,
                "Версия изменилась или экземпляр зарезервирован/выдан. Обновите состояние; автоматического повтора нет.");
        }
        await using (var projection = new NpgsqlCommand("""
            UPDATE book_search_projection SET
              "AvailableCopies" = (SELECT count(*) FROM book_copies WHERE "BookId" = @book AND "Status" = 0 AND NOT "IsHidden"),
              "MinimumPrice" = (SELECT min("LendingPrice") FROM book_copies WHERE "BookId" = @book AND "Status" = 0 AND NOT "IsHidden"),
              "UpdatedAt" = now() WHERE "BookId" = @book
            """, connection, tx))
        {
            projection.Parameters.AddWithValue("book", book);
            if (await projection.ExecuteNonQueryAsync(ct) != 1) throw new ReplicationLabException(409, "Проекция Catalog отсутствует; транзакция отменена.");
        }
        // A transport error during COMMIT is deliberately not retried.
        try { await tx.CommitAsync(ct); }
        catch (Exception ex) when (ex is NpgsqlException or OperationCanceledException)
        { throw new ReplicationLabException(503, "Исход COMMIT неизвестен. Перечитайте версию экземпляра; не повторяйте изменение автоматически."); }
        string? lsn = null;
        try
        {
            await using var wal = new NpgsqlCommand("SELECT pg_current_wal_flush_lsn()::text", connection);
            lsn = (string?)await wal.ExecuteScalarAsync(CancellationToken.None);
        }
        catch (NpgsqlException) { /* COMMIT succeeded; inability to observe WAL cannot undo it. */ }
        return new(input.CopyId, book, version, lsn);
    }
    public async Task<string> QuiesceAndFence(string node, CancellationToken ct)
    {
        // Durable startup guard first; then reject new app writes and drain existing transactions.
        await Exec(node, ct, "sh", "-c", "touch /control/$NODE_ID.fenced");
        await Scalar(node, "ALTER SYSTEM SET default_transaction_read_only = 'on'", ct);
        await Scalar(node, "SELECT pg_reload_conf()", ct);
        await Task.Delay(300, ct);
        await Scalar(node, "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE backend_type = 'client backend' AND pid <> pg_backend_pid()", ct);
        return (string)(await Scalar(node, "SELECT pg_current_wal_flush_lsn()::text", ct))!;
    }
    public async Task Stop(string node, CancellationToken ct) { CheckNode(node); await Docker(ct, "compose", "stop", "--timeout", "30", node); }
    public async Task<bool> IsStopped(string node, CancellationToken ct)
    {
        CheckNode(node);
        var id = await Docker(ct, "compose", "ps", "-aq", node);
        if (string.IsNullOrWhiteSpace(id)) throw new ReplicationLabException(503, "Контейнер узла не найден; fencing не подтверждён.");
        return await Docker(ct, "inspect", "--format", "{{.State.Running}}", id) == "false";
    }
    public async Task Promote(string node, CancellationToken ct)
    {
        if (!Equals(await Scalar(node, "SELECT pg_promote(true, 30)", ct), true)) throw new ReplicationLabException(503, "Promotion не завершён.");
        await Scalar(node, "ALTER SYSTEM RESET default_transaction_read_only", ct);
        await Scalar(node, "SELECT pg_reload_conf()", ct);
    }
    public async Task Recover(string node, string source, CancellationToken ct)
    {
        CheckNode(node); CheckNode(source); if (node == source) throw new ReplicationLabException(400, "Источник совпадает с целью.");
        if (!await IsStopped(node, ct)) throw new ReplicationLabException(409, "pg_rewind требует остановленный целевой узел.");
        await Docker(ct, "compose", "run", "--rm", "--no-deps", "--name", $"burmalda-lr5-maintenance-{node}", "-e", $"SOURCE_NODE={source}", "--entrypoint", "sh", node, "/opt/replication/rejoin.sh");
        await Start(node, ct);
    }
    public async Task RestoreSource(string node, CancellationToken ct)
    {
        CheckNode(node); await Stop(node, ct);
        await Docker(ct, "compose", "run", "--rm", "--no-deps", "--name", $"burmalda-lr5-maintenance-{node}", "--entrypoint", "sh", node, "/opt/replication/restore-source.sh");
        await Start(node, ct);
    }
    public async Task Start(string node, CancellationToken ct) { CheckNode(node); await Docker(ct, "compose", "start", node); }
    public async Task Pause(string node, bool paused, CancellationToken ct)
    { await Scalar(node, paused ? "SELECT pg_wal_replay_pause()" : "SELECT pg_wal_replay_resume()", ct); }
}
