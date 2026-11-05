using System.Text.Json;

namespace Burmalda.Web.Models;

public sealed record RaftNodeState(int NodeId,string Role,long Term,int? LeaderId,long CommitIndex,long LastApplied,long LastLogIndex,long SnapshotIndex,DateTimeOffset ObservedAtUtc,bool Authoritative);
public sealed record RaftNodeObservation(int NodeId,bool Available,RaftNodeState? State,DateTimeOffset ObservedAtUtc,string? Error);
public sealed record RaftControlView(RaftNodeState Node,RaftClusterState State);
public sealed class RaftClusterState
{
    public bool Initialized { get; set; }
    public string? Primary { get; set; }
    public string? FencedSource { get; set; }
    public string? RequiredLsn { get; set; }
    public Guid? ActiveOperation { get; set; }
    public Dictionary<Guid,RaftOperation> Operations { get; set; } = [];
}
public sealed record RaftOperationRequest(Guid CommandId,string Kind,string? Node=null);
public sealed class RaftOperation
{
    public required RaftOperationRequest Request { get; set; }
    public string Status { get; set; }="";
    public string Stage { get; set; }="";
    public string? Source { get; set; }
    public string? Target { get; set; }
    public string? Value { get; set; }
    public string? Error { get; set; }
}
public sealed record RaftLabSnapshot(RaftNodeObservation[] Nodes,int Majority,RaftControlView? Authoritative,
    RaftClusterState? State,ReplicationNode[] Postgres,JsonElement[] Diagnostic,string? Error,DateTimeOffset ObservedAtUtc);
public sealed record RaftLogRow(long Index,long Term,Guid? CommandId,string? Payload,bool Persisted,bool Committed,bool Applied);
public sealed record RaftLogPage(int NodeId,DateTimeOffset ObservedAtUtc,long? SnapshotIndex,RaftLogRow[] Entries);
public sealed record RaftEventRow(long Sequence,DateTimeOffset AtUtc,int NodeId,long Term,string Kind,string Detail);
public sealed record RaftEventPage(int NodeId,DateTimeOffset ObservedAtUtc,RaftEventRow[] Events);
