namespace Burmalda.Diagnostics;

// Diagnostic DTOs are also compiled into the WASM client. No protocol or storage dependencies.
public sealed record TraceLog(long Index, long Term, bool Committed, bool Applied);
public sealed record TraceState(int NodeId, string Role, long Term, int? LeaderId, long CommitIndex,
    long Applied, long LastIndex, int[] Votes, Dictionary<int,long> Match, TraceLog[] Log);
public sealed record TraceEntry(Guid RunId, long Sequence, DateTimeOffset AtUtc, int NodeId,
    string Kind, string Detail, int? From=null, int? To=null, Guid? RpcId=null,
    string? Rpc=null, long Term=0, long? Index=null, bool? Success=null, TraceState? State=null);
public sealed record TracePage(Guid RunId, long Cursor, bool Gap, TraceEntry[] Entries);
public sealed record TraceCheckpoint(TraceState State, int[] Blocked, bool AuthorityConfirmed);
public sealed record TraceWire(TracePage Trace, TraceCheckpoint Checkpoint);
public sealed record TraceNode(int NodeId, bool Available, TracePage? Trace, TraceCheckpoint? Checkpoint, string? Error);
public sealed record VisualizationFrame(DateTimeOffset AtUtc, TraceNode[] Nodes);
