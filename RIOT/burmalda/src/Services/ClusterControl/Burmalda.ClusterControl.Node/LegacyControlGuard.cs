using System.Text.Json;
using Burmalda.Raft;
using Burmalda.PostgresControl;
using Burmalda.ConcurrencyLab.Api;
using Microsoft.Data.Sqlite;

namespace Burmalda.ClusterControl;

// Read-only adoption gate. A fresh Docker cluster must not silently supersede unfinished host work.
public static class LegacyControlGuard
{
    public static void Check(string artifactsRoot)
    {
        if (!Directory.Exists(artifactsRoot)) return;
        var manifest = Path.Combine(artifactsRoot, "raft-runtime", "processes.json");
        if (File.Exists(manifest) && JsonDocument.Parse(File.ReadAllText(manifest)).RootElement.GetArrayLength() != 0)
            Reject("Остановите прежние управляющие процессы штатным Stop-RaftCluster.ps1.");
        var sessions = Path.Combine(artifactsRoot, "streaming-replication");
        var authority = Path.Combine(sessions, "authority.json");
        if (File.Exists(authority))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(authority));
            if (document.RootElement.TryGetProperty("fencedSource", out var source) && source.ValueKind != JsonValueKind.Null)
                Reject("Не завершено прежнее fencing ЛР5.");
        }
        if (Directory.Exists(sessions)) foreach (var path in Directory.EnumerateFiles(sessions, "session-*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("actionStatus", out var status) && status.GetString() == "Running")
                Reject("Не завершена прежняя операция ЛР5.");
        }
        var raft = Path.Combine(artifactsRoot, "raft-runtime");
        if (!Directory.Exists(raft)) return;
        foreach (var directory in Directory.EnumerateDirectories(raft, "node-*"))
        {
            var path = Path.Combine(directory, "raft.db");
            if (!File.Exists(path)) continue;
            ReadLegacy(path, db =>
            {
                using var command = db.CreateCommand(); command.CommandText = "SELECT json FROM hard_state WHERE id=1";
                if (command.ExecuteScalar() is not string json) return;
                var durable = RaftJson.Read<DurableState>(json); var state = RaftJson.Read<ClusterState>(durable.Machine);
                if (state.ActiveOperation is not null || state.FencedSource is not null || state.Operations.Values.Any(x => x.Status is "Running" or "NeedsInspection"))
                    Reject("Прежняя история Raft содержит незавершённую операцию. Сначала завершите её в локальном режиме.");
            });
        }
        var effects = Path.Combine(raft, "execution", "effects.db");
        if (File.Exists(effects))
        {
            ReadLegacy(effects, db =>
            {
                using var command = db.CreateCommand(); command.CommandText = "SELECT json FROM effects";
                using var reader = command.ExecuteReader();
                while (reader.Read()) if (RaftJson.Read<ExecutionRecord>(reader.GetString(0)).Status != "Done")
                    Reject("Прежний журнал исполнения содержит действие с неизвестным исходом.");
            });
        }
    }
    private static void ReadLegacy(string path, Action<SqliteConnection> read)
    {
        // Windows WAL/SHM cannot be memory-mapped on a read-only Docker Desktop bind mount.
        // Rebuild SHM only on a private copy; the stopped host's DB and WAL stay untouched.
        var temporary = Path.Combine(Path.GetTempPath(), "burmalda-legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var files = new[] { path, path + "-wal" }.Where(File.Exists).ToArray();
            var stamps = files.Select(file => (Path: file, Length: new FileInfo(file).Length, Time: File.GetLastWriteTimeUtc(file))).ToArray();
            var copy = Path.Combine(temporary, "legacy.db");
            File.Copy(path, copy);
            if (File.Exists(path + "-wal")) File.Copy(path + "-wal", copy + "-wal");
            if (stamps.Any(file => !File.Exists(file.Path) || new FileInfo(file.Path).Length != file.Length || File.GetLastWriteTimeUtc(file.Path) != file.Time) ||
                File.Exists(path + "-wal") != files.Contains(path + "-wal"))
                Reject("Прежний SQLite изменяется во время проверки. Остановите локальный кластер.");
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            db.Open(); read(db);
        }
        finally { Directory.Delete(temporary, true); }
    }
    private static void Reject(string reason) => throw new ReplicationLabException(409, reason + " Автоматический сброс запрещён; старые данные сохранены.");
}
