using Burmalda.Diagnostics;

namespace Burmalda.Raft;

// Best-effort, bounded and independent of the durable consensus log.
public sealed class TraceBuffer(int nodeId, int capacity = 8192)
{
    private readonly object gate = new();
    private readonly Queue<TraceEntry> entries = new();
    private long sequence;
    public Guid RunId { get; } = Guid.NewGuid();
    public void Add(string kind, string detail, int? from=null, int? to=null, Guid? rpcId=null,
        string? rpc=null, long term=0, long? index=null, bool? success=null, TraceState? state=null)
    {
        try
        {
            lock(gate)
            {
                entries.Enqueue(new(RunId,++sequence,DateTimeOffset.UtcNow,nodeId,kind,detail,from,to,rpcId,rpc,term,index,success,state));
                while(entries.Count>capacity) entries.Dequeue();
            }
        }
        catch(Exception) { /* Diagnostics must never alter protocol execution. */ }
    }
    public TracePage Read(Guid? run, long after, int take=500)
    {
        lock(gate)
        {
            var restarted=run.HasValue && run!=RunId;
            var start=restarted?0:Math.Max(0,after);
            var gap=restarted || (entries.TryPeek(out var first) && start<first.Sequence-1);
            var batch=entries.Where(x=>x.Sequence>start).Take(Math.Clamp(take,1,2000)).ToArray();
            return new(RunId,batch.LastOrDefault()?.Sequence??sequence,gap,batch);
        }
    }
}
