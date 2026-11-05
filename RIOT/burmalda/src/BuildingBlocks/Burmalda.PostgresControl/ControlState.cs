using Burmalda.Raft;

namespace Burmalda.PostgresControl;

public sealed record OperationRequest(Guid CommandId, string Kind, string? Node = null);
public sealed record ControlEntry(string Kind, Guid OperationId, OperationRequest? Request = null,
    string? Stage = null, string? Value = null, string? Source = null, string? Target = null, string? ExpectedStage = null);
public sealed record ControlAck(bool Accepted, string? Error = null);
public sealed class ClusterOperation
{
    public required OperationRequest Request { get; set; }
    public string Status { get; set; } = "Running";
    public string Stage { get; set; } = "Begin";
    public string? Source { get; set; }
    public string? Target { get; set; }
    public string? Value { get; set; }
    public string? Error { get; set; }
}
public sealed class ClusterState
{
    public bool Initialized { get; set; }
    public string? Primary { get; set; }
    public string? FencedSource { get; set; }
    public string? RequiredLsn { get; set; }
    public Guid? FenceOperation { get; set; }
    public Guid? ActiveOperation { get; set; }
    public Dictionary<Guid, ClusterOperation> Operations { get; set; } = [];
}
public sealed class ControlStateMachine : IRaftMachine
{
    public ClusterState State { get; private set; } = new();
    public string Export() => RaftJson.Write(State);
    public void Restore(string state)
    {
        var restored=RaftJson.Read<ClusterState>(state);
        if(restored is null || restored.Operations is null || restored.ActiveOperation is {} active && !restored.Operations.ContainsKey(active))
            throw new ArgumentException("Invalid cluster state snapshot");
        State=restored;
    }
    public string Apply(string command)
    {
        ControlEntry? entry;
        try { entry = RaftJson.Read<ControlEntry>(command); }
        catch (System.Text.Json.JsonException) { return RaftJson.Write(new ControlAck(false, "Invalid control entry")); }
        return RaftJson.Write(entry is null?new ControlAck(false,"Null control entry"):Apply(entry));
    }
    private ControlAck Apply(ControlEntry e)
    {
        if (e.Kind == "Bootstrap")
        {
            if (State.Initialized) return new(false, "Already initialized");
            State.Initialized = true; State.Primary = e.Value; return new(true);
        }
        if (!State.Initialized) return new(false, "PostgreSQL state is not initialized");
        if (e.Kind == "Begin")
        {
            if (e.Request is null || e.Request.CommandId != e.OperationId) return new(false, "Invalid operation");
            if (State.Operations.TryGetValue(e.OperationId, out var prior))
                return prior.Request == e.Request ? new(true) : new(false, "CommandId reused");
            if (State.ActiveOperation is not null) return new(false, "Another operation is active");
            State.Operations.Add(e.OperationId, new() { Request = e.Request }); State.ActiveOperation = e.OperationId; return new(true);
        }
        if (!State.Operations.TryGetValue(e.OperationId, out var op) || State.ActiveOperation != e.OperationId)
            return new(false, "Operation is not active");
        if (e.ExpectedStage is not null && op.Stage != e.ExpectedStage) return new(false, "Operation stage changed");
        if (e.Kind == "Stage")
        {
            op.Stage = e.Stage!; op.Value = e.Value; op.Status = "Running"; op.Error = null;
            if (e.Source is not null) op.Source = e.Source;
            if (e.Target is not null) op.Target = e.Target;
            if (e.Stage == "SourceFenced")
            { State.FencedSource = op.Source; State.RequiredLsn = e.Value; State.FenceOperation = e.OperationId; }
            if (e.Stage == "PrimaryActivated") State.Primary = op.Target;
            if (e.Stage == "Recovered")
            {
                // Restarting the promoted primary does not rejoin its still-fenced predecessor.
                if(State.FencedSource==op.Target) { State.FencedSource = null; State.RequiredLsn = null; State.FenceOperation = null; }
                State.Primary = op.Source ?? State.Primary;
            }
            return new(true);
        }
        if (e.Kind is "Complete" or "Failed" or "NeedsInspection")
        {
            op.Status = e.Kind == "Complete" ? "Completed" : e.Kind;
            op.Error = e.Kind == "Complete" ? null : e.Value;
            if (e.Kind == "Complete") op.Value = e.Value;
            if (e.Kind != "NeedsInspection") State.ActiveOperation = null;
            return new(true);
        }
        return new(false, "Unknown control entry");
    }
}
