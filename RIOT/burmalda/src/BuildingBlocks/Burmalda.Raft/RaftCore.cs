namespace Burmalda.Raft;

// Deterministic, single-threaded protocol. The host serializes all calls; I/O happens outside it.
public sealed class RaftCore
{
    private sealed class Peer
    {
        public long Next, Match, Sequence, SentAt;
        public object? Pending;
    }
    private sealed record ReadRound(long Term, long Index, HashSet<int> Acks);
    private readonly RaftOptions options;
    private readonly IRaftStorage storage;
    private readonly IRaftMachine machine;
    private readonly Func<int, int, int> random;
    private readonly Dictionary<int, Peer> peers;
    private readonly HashSet<int> votes = [];
    private readonly List<Outbound> messages = [];
    private readonly Queue<RaftEvent> events = [];
    private readonly Dictionary<long, ReadRound> reads = [];
    private DurableState state;
    private long now, electionDeadline, heartbeatAt, sequence, eventSequence, readSequence;
    public RaftRole Role { get; private set; } = RaftRole.Follower;
    public int? LeaderId { get; private set; }
    public long Term => state.Term;
    public long CommitIndex => state.CommitIndex;
    public long LastIndex => state.Log.LastOrDefault()?.Index ?? state.Snapshot.Index;
    public int Majority => options.Members.Count / 2 + 1;
    public RaftCore(RaftOptions options, IRaftStorage storage, IRaftMachine machine,
        Func<int, int, int>? random = null, long initialTime = 0)
    {
        if (options.Members.Count != 5 || !options.Members.ContainsKey(options.NodeId) ||
            options.HeartbeatMs <= 0 || options.ElectionMinMs <= options.HeartbeatMs ||
            options.ElectionMaxMs <= options.ElectionMinMs || options.SnapshotEvery < 1)
            throw new ArgumentException("Raft requires five members and valid timer/snapshot settings.");
        this.options = options; this.storage = storage; this.machine = machine;
        this.random = random ?? Random.Shared.Next;
        var identity = options.ClusterId + "|" + string.Join('|', options.Members.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}")) + $"|node={options.NodeId}";
        state = storage.Load() ?? new DurableState { Identity = identity, Machine = machine.Export(), Snapshot = new(0, 0, machine.Export(), []) };
        if (state.Identity != identity) throw new InvalidDataException("Stored Raft cluster membership or node identity differs from configuration.");
        machine.Restore(state.Machine);
        if (state.Applied != state.CommitIndex || state.CommitIndex > LastIndex || state.Snapshot.Index > state.Applied)
            throw new InvalidDataException("Invalid durable Raft state.");
        peers = options.Members.Keys.Where(x => x != options.NodeId).ToDictionary(x => x, _ => new Peer());
        now = initialTime; ResetElection(); Save();
    }
    public RaftStatus Status() => new(options.NodeId, Role.ToString(), Term, LeaderId,
        state.CommitIndex, state.Applied, LastIndex, state.Snapshot.Index, DateTimeOffset.UtcNow);
    public string MachineState => machine.Export();
    public IReadOnlyList<LogEntry> Log => state.Log.ToArray();
    public IReadOnlyList<RaftEvent> Events => events.ToArray();
    public long EventSequence => eventSequence;
    public Burmalda.Diagnostics.TraceState TraceState() => new(options.NodeId,Role.ToString(),Term,LeaderId,
        state.CommitIndex,state.Applied,LastIndex,votes.Order().ToArray(),
        peers.ToDictionary(x=>x.Key,x=>x.Value.Match),
        state.Log.TakeLast(3).Select(x=>new Burmalda.Diagnostics.TraceLog(x.Index,x.Term,x.Index<=state.CommitIndex,x.Index<=state.Applied)).ToArray());
    public CommandResult? Result(Guid id) => state.Results.GetValueOrDefault(id);
    public long TermAt(long index) => index == state.Snapshot.Index ? state.Snapshot.Term : state.Log.FirstOrDefault(e => e.Index == index)?.Term ?? -1;
    public Outbound[] DrainMessages() { var result = messages.ToArray(); messages.Clear(); return result; }
    private void Save() { state.Machine = machine.Export(); storage.Save(state); }
    private void Event(string kind, string detail)
    {
        events.Enqueue(new(++eventSequence, DateTimeOffset.UtcNow, options.NodeId, Term, kind, detail));
        while (events.Count > 2000) events.Dequeue();
    }
    private void ResetElection() => electionDeadline = now + random(options.ElectionMinMs, options.ElectionMaxMs);
    private void Follow(long term, int? leader = null)
    {
        if (term > Term) { state.Term = term; state.VotedFor = null; Save(); }
        if (Role != RaftRole.Follower) Event("Follower", $"leader={leader}");
        Role = RaftRole.Follower; LeaderId = leader; reads.Clear(); votes.Clear();
        foreach (var p in peers.Values) p.Pending = null;
        ResetElection();
    }
    public void Tick(long milliseconds)
    {
        if (milliseconds < now) throw new ArgumentException("Monotonic time required.");
        now = milliseconds;
        if (Role != RaftRole.Leader)
        {
            if (now >= electionDeadline) Elect();
            return;
        }
        foreach (var p in peers.Values)
            if (p.Pending is not null && now - p.SentAt >= options.RpcTimeoutMs) p.Pending = null;
        if (now >= heartbeatAt) { Broadcast(); heartbeatAt = now + options.HeartbeatMs; }
    }
    private void Elect()
    {
        Role = RaftRole.Candidate; LeaderId = null; state.Term++; state.VotedFor = options.NodeId;
        votes.Clear(); votes.Add(options.NodeId); reads.Clear(); ResetElection(); Save();
        Event("Election", "RequestVote started");
        foreach (var target in peers.Keys)
            messages.Add(new(target, ++sequence, new VoteRequest(Term, options.NodeId, LastIndex, TermAt(LastIndex))));
    }
    public VoteResponse RequestVote(VoteRequest request)
    {
        if (!options.Members.ContainsKey(request.CandidateId) || request.Term < 0 || request.LastLogIndex < 0 || request.LastLogTerm < 0)
            throw new ArgumentException("Invalid vote request.");
        if (request.Term < Term) return new(Term, false);
        if (request.Term > Term) Follow(request.Term);
        var upToDate = request.LastLogTerm > TermAt(LastIndex) ||
            request.LastLogTerm == TermAt(LastIndex) && request.LastLogIndex >= LastIndex;
        var granted = upToDate && (state.VotedFor is null || state.VotedFor == request.CandidateId);
        if (granted) { state.VotedFor = request.CandidateId; ResetElection(); Save(); Event("Vote", $"YES Node {request.CandidateId}"); }
        return new(Term, granted);
    }
    public void ReceiveVote(int peer, VoteRequest request, VoteResponse response)
    {
        if (!peers.ContainsKey(peer)) return;
        if (response.Term > Term) { Follow(response.Term); return; }
        if (Role != RaftRole.Candidate || request.Term != Term || response.Term != Term || !response.Granted) return;
        votes.Add(peer);
        Event("VoteReceived", $"YES Node {peer}; votes={votes.Count}; majority={Majority}");
        if (votes.Count < Majority) return;
        Role = RaftRole.Leader; LeaderId = options.NodeId; reads.Clear();
        foreach (var p in peers.Values) { p.Match = 0; p.Next = LastIndex + 1; p.Pending = null; }
        state.Log.Add(new(LastIndex + 1, Term, null)); Save();
        Event("Leader", "Majority elected leader; appended current-term no-op");
        Broadcast(); heartbeatAt = now + options.HeartbeatMs;
    }
    private void Send(int target)
    {
        var p = peers[target]; if (p.Pending is not null) return;
        object request;
        if (p.Next <= state.Snapshot.Index) request = new SnapshotRequest(Term, options.NodeId, state.Snapshot);
        else request = new AppendRequest(Term, options.NodeId, p.Next - 1, TermAt(p.Next - 1),
            state.Log.Where(e => e.Index >= p.Next).Take(64).ToArray(), state.CommitIndex,
            reads.Count == 0 ? 0 : reads.Keys.Max());
        p.Pending = request; p.Sequence = ++sequence; p.SentAt = now;
        messages.Add(new(target, p.Sequence, request));
    }
    private void Broadcast() { foreach (var target in peers.Keys) Send(target); }
    public AppendResponse AppendEntries(AppendRequest request)
    {
        if (!options.Members.ContainsKey(request.LeaderId) || request.Term < 0 || request.PrevLogIndex < 0 || request.LeaderCommit < 0 ||
            request.Entries is null || request.Entries.Length > 64 || request.Entries.Where((e, i) => e is null || e.Index != request.PrevLogIndex + i + 1 || e.Term < 0 || e.Term > request.Term || e.Command?.CommandId == Guid.Empty || e.Command is { Payload: null }).Any())
            throw new ArgumentException("Invalid AppendEntries.");
        if (request.Term < Term) return new(Term, false, 0, LastIndex + 1, request.ReadContext);
        Follow(request.Term, request.LeaderId);
        if (TermAt(request.PrevLogIndex) != request.PrevLogTerm)
            return new(Term, false, 0, request.PrevLogIndex > LastIndex ? LastIndex + 1 : Math.Max(state.Snapshot.Index + 1, request.PrevLogIndex), request.ReadContext);
        var changed = false;
        foreach (var entry in request.Entries)
        {
            var existing = state.Log.FirstOrDefault(e => e.Index == entry.Index);
            if (existing is not null && existing.Term != entry.Term)
            {
                if (entry.Index <= state.CommitIndex) return new(Term, false, 0, state.CommitIndex + 1, request.ReadContext);
                state.Log.RemoveAll(e => e.Index >= entry.Index); existing = null; changed = true;
            }
            if (existing is null) { state.Log.Add(entry); changed = true; }
        }
        var nextCommit = Math.Max(state.CommitIndex, Math.Min(request.LeaderCommit, request.PrevLogIndex + request.Entries.Length));
        if(nextCommit != state.CommitIndex) { state.CommitIndex = nextCommit; changed = true; }
        if(changed) { Apply(); Save(); }
        return new(Term, true, request.PrevLogIndex + request.Entries.Length, LastIndex + 1, request.ReadContext);
    }
    public void ReceiveAppend(int peer, long requestSequence, AppendRequest request, AppendResponse response)
    {
        if (!peers.TryGetValue(peer, out var p)) return;
        if (response.Term > Term) { Follow(response.Term); return; }
        if (Role != RaftRole.Leader || request.Term != Term || response.Term != Term || p.Sequence != requestSequence || !ReferenceEquals(p.Pending, request)) return;
        p.Pending = null;
        if (!response.Success) { p.Next = Math.Max(p.Match + 1, Math.Min(p.Next - 1, response.NextIndex)); Send(peer); return; }
        var acknowledged = request.PrevLogIndex + request.Entries.Length;
        p.Match = Math.Max(p.Match, acknowledged); p.Next = p.Match + 1;
        if(request.Entries.Length>0) Event("Acknowledged", $"Node {peer}; index={p.Match}");
        foreach (var round in reads.Where(x => x.Key <= response.ReadContext).Select(x => x.Value)) round.Acks.Add(peer);
        AdvanceCommit();
        if (p.Next <= LastIndex) Send(peer);
    }
    private void AdvanceCommit()
    {
        var index = peers.Values.Select(p => p.Match).Append(LastIndex).OrderDescending().ElementAt(Majority - 1);
        if (index <= state.CommitIndex || TermAt(index) != Term) return;
        state.CommitIndex = index; Apply(); Save(); Event("Commit", $"index={index}; majority={Majority}");
    }
    private void Apply()
    {
        while (state.Applied < state.CommitIndex)
        {
            var entry = state.Log.Single(e => e.Index == state.Applied + 1);
            if (entry.Command is { } command && !state.Results.ContainsKey(command.CommandId))
                state.Results.Add(command.CommandId, new(command.Payload, machine.Apply(command.Payload)));
            state.Applied = entry.Index;
        }
        if (state.Applied - state.Snapshot.Index >= options.SnapshotEvery)
        {
            state.Snapshot = new(state.Applied, TermAt(state.Applied), machine.Export(), new(state.Results));
            state.Log.RemoveAll(e => e.Index <= state.Snapshot.Index);
            Event("Snapshot", $"index={state.Snapshot.Index}");
        }
    }
    public long Propose(RaftCommand command)
    {
        if (Role != RaftRole.Leader) throw new RaftUnavailableException($"Not leader; leader={LeaderId}");
        if (command.CommandId == Guid.Empty || command.Payload is null || command.Payload.Length > 65536) throw new ArgumentException("Invalid command.");
        if (state.Results.TryGetValue(command.CommandId, out var result))
        {
            if (result.Payload != command.Payload) throw new RaftConflictException("CommandId reused with different content.");
            return state.Applied;
        }
        var existing = state.Log.FirstOrDefault(e => e.Command?.CommandId == command.CommandId);
        if (existing is not null)
        {
            if (existing.Command!.Payload != command.Payload) throw new RaftConflictException("CommandId reused with different content.");
            return existing.Index;
        }
        var index = LastIndex + 1; state.Log.Add(new(index, Term, command)); Save(); Broadcast();
        Event("Propose", $"index={index}; command={command.CommandId}"); return index;
    }
    public long BeginRead()
    {
        if (Role != RaftRole.Leader || TermAt(state.CommitIndex) != Term)
            throw new RaftUnavailableException("Current-term leadership is not committed.");
        var id = ++readSequence; reads.Add(id, new(Term, state.CommitIndex, [options.NodeId])); Broadcast(); return id;
    }
    public bool ReadConfirmed(long id) => Role == RaftRole.Leader && reads.TryGetValue(id, out var r) &&
        r.Term == Term && r.Acks.Count >= Majority && state.Applied >= r.Index;
    public void EndRead(long id) => reads.Remove(id);
    public SnapshotResponse InstallSnapshot(SnapshotRequest request)
    {
        if (!options.Members.ContainsKey(request.LeaderId) || request.Snapshot is null || request.Snapshot.Results is null ||
            request.Snapshot.Machine is null || request.Snapshot.Index < 0 || request.Snapshot.Term < 0 || request.Snapshot.Term > request.Term)
            throw new ArgumentException("Invalid snapshot.");
        if (request.Term < Term) return new(Term, state.Snapshot.Index);
        Follow(request.Term, request.LeaderId);
        var snapshot = request.Snapshot;
        if (snapshot.Index <= state.Applied) return new(Term, state.Applied);
        var before = machine.Export();
        try { machine.Restore(snapshot.Machine); }
        catch (Exception e) when (e is System.Text.Json.JsonException or ArgumentException)
        { machine.Restore(before); throw new ArgumentException("Invalid snapshot state machine payload", e); }
        if (TermAt(snapshot.Index) == snapshot.Term) state.Log.RemoveAll(e => e.Index <= snapshot.Index);
        else state.Log.Clear();
        state.Snapshot = snapshot; state.Results = new(snapshot.Results); state.CommitIndex = snapshot.Index; state.Applied = snapshot.Index;
        Save(); Event("InstallSnapshot", $"index={snapshot.Index}"); return new(Term, snapshot.Index);
    }
    public void ReceiveSnapshot(int peer, long requestSequence, SnapshotRequest request, SnapshotResponse response)
    {
        if (!peers.TryGetValue(peer, out var p)) return;
        if (response.Term > Term) { Follow(response.Term); return; }
        if (Role != RaftRole.Leader || request.Term != Term || response.Term != Term || p.Sequence != requestSequence || !ReferenceEquals(p.Pending, request)) return;
        p.Pending = null; p.Match = Math.Max(p.Match, request.Snapshot.Index); p.Next = p.Match + 1; Send(peer);
    }
}
