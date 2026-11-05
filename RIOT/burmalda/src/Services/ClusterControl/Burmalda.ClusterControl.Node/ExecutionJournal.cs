using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Burmalda.Raft;
using Burmalda.PostgresControl;

namespace Burmalda.ClusterControl;

public sealed class NeedsInspectionException(string message) : Exception(message);
public sealed class ExternalActionRunningException(string message) : Exception(message);
public sealed class ExecutionRecord
{
    public string Status { get; set; } = "Running";
    public string? Value { get; set; }
    public List<ProcessIdentity> Processes { get; set; } = [];
}
// Shared only by local effectors, never by Raft participants' consensus storage.
public sealed class ExecutionJournal
{
    private readonly string root;
    public ExecutionJournal(string root) { this.root=Path.GetFullPath(root); Directory.CreateDirectory(this.root); }
    public FileStream? TryLock()
    {
        try { return new(Path.Combine(root,"cluster.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); }
        catch(IOException) { return null; }
    }
    public void BindCluster(string identity)
    {
        using var db=Open();using var tx=db.BeginTransaction();using var command=db.CreateCommand();command.Transaction=tx;
        command.CommandText="CREATE TABLE IF NOT EXISTS equipment_owner(id INTEGER PRIMARY KEY CHECK(id=1),identity TEXT NOT NULL); SELECT identity FROM equipment_owner WHERE id=1;";
        var existing=command.ExecuteScalar() as string;
        if(existing is not null && existing!=identity)throw new InvalidOperationException("This Docker project is owned by another Raft configuration; equipment mutations are blocked.");
        command.CommandText="INSERT OR IGNORE INTO equipment_owner(id,identity) VALUES(1,$identity)";command.Parameters.AddWithValue("$identity",identity);command.ExecuteNonQuery();tx.Commit();
    }
    private SqliteConnection Open()
    {
        var connection=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=Path.Combine(root,"effects.db"),Pooling=false }.ToString()); connection.Open();
        using var command=connection.CreateCommand(); command.CommandText="PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS effects(id TEXT PRIMARY KEY,json TEXT NOT NULL);"; command.ExecuteNonQuery(); return connection;
    }
    private ExecutionRecord? Read(string id)
    {
        using var db=Open(); using var command=db.CreateCommand(); command.CommandText="SELECT json FROM effects WHERE id=$id"; command.Parameters.AddWithValue("$id",id);
        return command.ExecuteScalar() is string json?RaftJson.Read<ExecutionRecord>(json):null;
    }
    private void Save(string id,ExecutionRecord record)
    {
        using var db=Open(); using var tx=db.BeginTransaction(); using var command=db.CreateCommand(); command.Transaction=tx;
        command.CommandText="INSERT INTO effects VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json";
        command.Parameters.AddWithValue("$id",id); command.Parameters.AddWithValue("$json",RaftJson.Write(record)); command.ExecuteNonQuery(); tx.Commit();
    }
    public bool HasUnresolved(Guid operation)
    {
        using var db=Open();using var command=db.CreateCommand();
        command.CommandText="SELECT json FROM effects WHERE id LIKE $prefix";command.Parameters.AddWithValue("$prefix",operation+":%");
        using var reader=command.ExecuteReader();
        while(reader.Read())if(RaftJson.Read<ExecutionRecord>(reader.GetString(0)).Status!="Done")return true;
        return false;
    }
    public HashSet<Guid> CompletedRecoveries(IEnumerable<Guid> committedCompleted)
        => committedCompleted.Where(id => Read($"{id}:Recover")?.Status == "Done" && !HasUnresolved(id)).ToHashSet();
    private static bool StillRunning(ProcessIdentity identity)
    {
        try { using var p=Process.GetProcessById(identity.Pid); return !p.HasExited && p.StartTime.ToUniversalTime()==identity.StartedUtc; }
        catch(ArgumentException) { return false; }
        catch(InvalidOperationException) { return false; }
    }
    public async Task<string> Run(Guid operation,string step,Func<Task<string>> action,Func<Task<string?>> reconcile)
    {
        var key=$"{operation}:{step}"; var prior=Read(key);
        if(prior?.Status=="Done") return prior.Value!;
        if(prior is not null)
        {
            if(prior.Processes.Any(StillRunning)) throw new ExternalActionRunningException("Предыдущий Docker CLI ещё работает; ожидаем его завершения.");
            using var reconciliationScope=ExecutionScope.Enter(operation,p=> { prior.Processes.Add(p);Save(key,prior); });
            var recovered=await reconcile();
            if(recovered is null) throw new NeedsInspectionException($"Исход действия {key} не установлен. Последующие мутации заблокированы.");
            prior.Status="Done"; prior.Value=recovered; Save(key,prior); return recovered;
        }
        var record=new ExecutionRecord(); Save(key,record);
        using var scope=ExecutionScope.Enter(operation,p=> { record.Processes.Add(p); Save(key,record); });
        try { var value=await action(); record.Status="Done"; record.Value=value; Save(key,record); return value; }
        catch(OperationCanceledException) { throw; }
        catch(Exception e) { throw new NeedsInspectionException($"Действие {step} было начато, исход требует проверки: {e.Message}"); }
    }
}
