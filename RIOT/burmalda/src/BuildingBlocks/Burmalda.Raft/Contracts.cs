using System.Text.Json;

namespace Burmalda.Raft;

public static class RaftJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options)!;
}

public sealed record RaftOptions(int NodeId, string ClusterId, Dictionary<int, string> Members,
    int HeartbeatMs = 200, int ElectionMinMs = 1500, int ElectionMaxMs = 3000,
    int RpcTimeoutMs = 500, int SnapshotEvery = 256);
public enum RaftRole { Follower, Candidate, Leader }
public sealed record RaftCommand(Guid CommandId, string Payload);
public sealed record CommandResult(string Payload, string Result);
public sealed record LogEntry(long Index, long Term, RaftCommand? Command);
public sealed record RaftSnapshot(long Index, long Term, string Machine,
    Dictionary<Guid, CommandResult> Results);
public sealed class DurableState
{
    public string Identity { get; set; } = "";
    public long Term { get; set; }
    public int? VotedFor { get; set; }
    public List<LogEntry> Log { get; set; } = [];
    public long CommitIndex { get; set; }
    public long Applied { get; set; }
    public string Machine { get; set; } = "{}";
    public Dictionary<Guid, CommandResult> Results { get; set; } = [];
    public RaftSnapshot Snapshot { get; set; } = new(0, 0, "{}", []);
}
public interface IRaftMachine
{
    string Apply(string command);
    string Export();
    void Restore(string state);
}
public interface IRaftStorage : IDisposable
{
    DurableState? Load();
    void Save(DurableState state);
}
public sealed record VoteRequest(long Term, int CandidateId, long LastLogIndex, long LastLogTerm);
public sealed record VoteResponse(long Term, bool Granted);
public sealed record AppendRequest(long Term, int LeaderId, long PrevLogIndex, long PrevLogTerm,
    LogEntry[] Entries, long LeaderCommit, long ReadContext = 0);
public sealed record AppendResponse(long Term, bool Success, long MatchIndex, long NextIndex, long ReadContext);
public sealed record SnapshotRequest(long Term, int LeaderId, RaftSnapshot Snapshot);
public sealed record SnapshotResponse(long Term, long MatchIndex);
public sealed record Outbound(int Target, long Sequence, object Request);
public sealed record RaftEvent(long Sequence, DateTimeOffset AtUtc, int NodeId, long Term, string Kind, string Detail);
public sealed record RaftStatus(int NodeId, string Role, long Term, int? LeaderId, long CommitIndex,
    long LastApplied, long LastLogIndex, long SnapshotIndex, DateTimeOffset ObservedAtUtc, bool Authoritative = false);
public sealed class RaftUnavailableException(string message) : Exception(message);
public sealed class RaftConflictException(string message) : Exception(message);
