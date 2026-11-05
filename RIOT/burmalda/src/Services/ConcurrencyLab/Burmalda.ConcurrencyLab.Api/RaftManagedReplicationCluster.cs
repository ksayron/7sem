using Burmalda.PostgresControl;
using Burmalda.Raft;

namespace Burmalda.ConcurrencyLab.Api;

// Catalog SQL remains ordinary PostgreSQL SQL. Every infrastructure mutation is a Raft operation.
public sealed class RaftManagedReplicationCluster(PostgresReplicationCluster raw,RaftControlClient control) : IReplicationCluster
{
    public Task<ReplicationNode> Observe(string node,CancellationToken ct)=>raw.Observe(node,ct);
    public Task<CopyCommit> Update(string node,CopyAvailabilityRequest input,CancellationToken ct)=>raw.Update(node,input,ct);
    private async Task<ClusterOperation> Execute(string kind,string? node,CancellationToken ct)
    {
        try { return await control.Execute(kind,node,ct); }
        catch(RaftConflictException e) { throw new ReplicationLabException(409,e.Message); }
        catch(Exception e) when(e is RaftUnavailableException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        { throw new ReplicationLabException(503,e.Message); }
    }
    public async Task<string> QuiesceAndFence(string node,CancellationToken ct)=>(await Execute("Fence",node,ct)).Value!;
    public async Task Stop(string node,CancellationToken ct)=>await Execute("Stop",node,ct);
    public Task<bool> IsStopped(string node,CancellationToken ct)=>raw.IsStopped(node,ct);
    public async Task Promote(string node,CancellationToken ct)=>await Execute("Promote",node,ct);
    public async Task Recover(string node,string source,CancellationToken ct)=>await Execute("Recover",node,ct);
    public async Task RestoreSource(string node,CancellationToken ct)=>await Execute("Recover",node,ct);
    public async Task Start(string node,CancellationToken ct)=>await Execute("Recover",node,ct);
    public async Task Pause(string node,bool paused,CancellationToken ct)=>await Execute(paused?"Pause":"Resume",node,ct);
}
