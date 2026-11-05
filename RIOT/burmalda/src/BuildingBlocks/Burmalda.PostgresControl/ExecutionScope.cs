using System.Diagnostics;

namespace Burmalda.PostgresControl;

public sealed record ProcessIdentity(int Pid, DateTime StartedUtc, string Executable, string Arguments);
public sealed class ExecutionScope(Guid operationId, Action<ProcessIdentity> record) : IDisposable
{
    private static readonly AsyncLocal<ExecutionScope?> current = new();
    public static ExecutionScope? Current => current.Value;
    public Guid OperationId { get; } = operationId;
    public Action<ProcessIdentity> Record { get; } = record;
    public static ExecutionScope Enter(Guid operationId, Action<ProcessIdentity> record)
    { var scope=new ExecutionScope(operationId,record); current.Value=scope; return scope; }
    public void Dispose() => current.Value=null;
    public void Started(Process process,ProcessStartInfo info)
        => Record(new(process.Id,process.StartTime.ToUniversalTime(),info.FileName,string.Join(' ',info.ArgumentList)));
}
