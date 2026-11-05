using Microsoft.Data.Sqlite;

namespace Burmalda.Raft;

// One database per participant. A single transaction persists hard state, log and application state.
public sealed class SqliteRaftStorage : IRaftStorage
{
    private readonly SqliteConnection connection;
    private readonly FileStream ownership;
    public SqliteRaftStorage(string path)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ownership = new FileStream(path + ".owner", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS hard_state(id INTEGER PRIMARY KEY CHECK(id=1), json TEXT NOT NULL);";
        command.ExecuteNonQuery();
    }
    public DurableState? Load()
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT json FROM hard_state WHERE id=1";
        return command.ExecuteScalar() is string json ? RaftJson.Read<DurableState>(json) : null;
    }
    public void Save(DurableState state)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO hard_state(id,json) VALUES(1,$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json";
        command.Parameters.AddWithValue("$json", RaftJson.Write(state)); command.ExecuteNonQuery(); transaction.Commit();
    }
    public void Dispose() { connection.Dispose(); ownership.Dispose(); }
}
