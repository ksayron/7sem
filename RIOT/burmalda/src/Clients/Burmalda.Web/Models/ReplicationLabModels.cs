namespace Burmalda.Web.Models;

public sealed record ReplicationBook(Guid Id, string Title, int Available);
public sealed record ReplicationCopy(Guid Id, Guid BookId, string Title, string Status, bool IsHidden, long Version);
public sealed record ReplicationNode(string Id, string State, string Role, bool ReadOnly, bool Fenced,
    string? WalLsn, string? ReplayLsn, bool ReplayPaused, string? StreamingState, DateTimeOffset? ObservedAtUtc,
    IReadOnlyList<ReplicationBook> Books, IReadOnlyList<ReplicationCopy> Copies, string? Error);
public sealed record ReplicationLog(long Sequence, DateTimeOffset AtUtc, string ClientId, string Operation, string Instance, string Result, string Detail);
public sealed record ReplicationOperation(long Number, string ClientId, string Operation, Guid? CopyId,
    string? UsedInstance, bool? Committed, string Replication, string Result, string Message);
public sealed record ReplicationMilestone(string Label, string? Writer, IReadOnlyList<ReplicationNode> Nodes);
public sealed record ReplicationLabSnapshot(Guid Id, string? ActiveInstance, bool Consistent, long? ReplayLagBytes,
    bool ClusterBusy, string ActionStatus, string? ActionError, IReadOnlyList<ReplicationNode> Nodes,
    IReadOnlyList<ReplicationOperation> Operations, IReadOnlyList<ReplicationLog> Events, IReadOnlyList<ReplicationMilestone> Milestones);
