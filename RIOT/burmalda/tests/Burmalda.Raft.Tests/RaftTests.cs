using Burmalda.Raft;
using Xunit;

namespace Burmalda.Raft.Tests;

public sealed class TestMachine : IRaftMachine
{
    public List<string> Values { get; private set; } = [];
    public string Apply(string command) { Values.Add(command); return command; }
    public string Export() => RaftJson.Write(Values);
    public void Restore(string state) => Values = RaftJson.Read<List<string>>(state);
}
public sealed class MemoryStorage : IRaftStorage
{
    public int Writes { get; private set; }
    private string? json;
    public DurableState? Load() => json is null ? null : RaftJson.Read<DurableState>(json);
    public void Save(DurableState state) { json = RaftJson.Write(state); Writes++; }
    public void Dispose() { }
}
public sealed class Simulation
{
    public readonly Dictionary<int, RaftCore> Nodes = [];
    public readonly Dictionary<int, MemoryStorage> Stores = [];
    public readonly HashSet<int> Down = [];
    public readonly HashSet<(int, int)> Blocked = [];
    public readonly Dictionary<int, TestMachine> Machines = [];
    public readonly List<(int Sender, Outbound Message)> Queue = [];
    public long Now;
    private readonly int snapshotEvery;
    public Simulation(int snapshotEvery = 256) { this.snapshotEvery = snapshotEvery; for (var i = 1; i <= 5; i++) { Stores[i] = new(); Restart(i); } }
    public void Restart(int id)
    {
        Machines[id] = new(); Down.Remove(id);
        Nodes[id] = new(new(id, "test", Enumerable.Range(1, 5).ToDictionary(x => x, x => $"node-{x}"),
            20, 200, 900, 80, snapshotEvery), Stores[id], Machines[id], (a, _) => a + id * 43, Now);
    }
    public RaftCore Leader => Nodes.Values.Single(n => !Down.Contains(n.Status().NodeId) && n.Role == RaftRole.Leader && Enumerable.Range(1,5).Count(p => !Down.Contains(p) && !Blocked.Contains((n.Status().NodeId,p))) >= 3);
    public void Collect() { foreach (var (id, node) in Nodes) if (!Down.Contains(id)) Queue.AddRange(node.DrainMessages().Select(m => (id, m))); }
    public void Deliver(int at = 0, bool duplicate = false)
    {
        var (sender, m) = Queue[at]; Queue.RemoveAt(at);
        if (Down.Contains(sender) || Down.Contains(m.Target) || Blocked.Contains((sender, m.Target))) return;
        void Once()
        {
            var target = Nodes[m.Target]; var source = Nodes[sender];
            switch (m.Request)
            {
                case VoteRequest v: var vr = target.RequestVote(v); if (!Blocked.Contains((m.Target,sender))) source.ReceiveVote(m.Target,v,vr); break;
                case AppendRequest a: var ar = target.AppendEntries(a); if (!Blocked.Contains((m.Target,sender))) source.ReceiveAppend(m.Target,m.Sequence,a,ar); break;
                case SnapshotRequest s: var sr = target.InstallSnapshot(s); if (!Blocked.Contains((m.Target,sender))) source.ReceiveSnapshot(m.Target,m.Sequence,s,sr); break;
            }
        }
        Once(); if (duplicate) Once(); Collect();
    }
    public void Pump() { Collect(); var count = 0; while (Queue.Count > 0) { if (++count > 10000) throw new Exception("RPC loop"); Deliver(); } }
    public void Step(int duration = 50)
    {
        Now += duration; foreach (var (id, n) in Nodes) if (!Down.Contains(id)) n.Tick(Now); Pump();
    }
    public void Advance(int duration = 1200) { for (var i = 0; i < duration; i += 20) Step(20); }
    public RaftCommand Commit(string value)
    {
        var command = new RaftCommand(Guid.NewGuid(), value); Leader.Propose(command); Pump(); Advance(100); return command;
    }
    public void Partition(params int[] minority)
    {
        Blocked.Clear(); foreach (var a in Enumerable.Range(1,5)) foreach (var b in Enumerable.Range(1,5))
            if (minority.Contains(a) != minority.Contains(b)) Blocked.Add((a,b));
    }
}
public sealed class RaftTests
{
    [Fact] public void Idle_heartbeats_do_not_rewrite_unchanged_persistent_state()
    {
        var s=new Simulation();s.Advance();s.Commit("durable");s.Advance();
        var writes=s.Stores.Values.Sum(x=>x.Writes);s.Advance(2000);
        Assert.Equal(writes,s.Stores.Values.Sum(x=>x.Writes));
    }
    [Fact] public void Split_vote_is_resolved_by_a_later_election_timeout()
    {
        var s=new Simulation();s.Now=500;
        foreach(var node in s.Nodes.Values)node.Tick(s.Now);
        s.Pump();Assert.DoesNotContain(s.Nodes.Values,n=>n.Role==RaftRole.Leader);
        s.Advance();Assert.Single(s.Nodes.Values,n=>n.Role==RaftRole.Leader);
    }
    [Fact] public void Election_and_replication_apply_identical_history()
    {
        var s = new Simulation(); s.Advance(); Assert.Single(s.Nodes.Values, n => n.Role == RaftRole.Leader);
        var c = s.Commit("A"); Assert.All(s.Nodes.Values, n => Assert.Equal("A", n.Result(c.CommandId)!.Result));
    }
    [Fact] public void Vote_is_durable_and_stale_candidate_is_rejected()
    {
        var s = new Simulation(); var request = new VoteRequest(3, 2, 0, 0);
        Assert.True(s.Nodes[1].RequestVote(request).Granted); s.Restart(1);
        Assert.False(s.Nodes[1].RequestVote(request with { CandidateId = 3 }).Granted);
        s.Advance(); s.Commit("current");
        Assert.False(s.Nodes[1].RequestVote(new(s.Nodes[1].Term + 1, 3, 0, 0)).Granted);
    }
    [Theory] [InlineData(1)] [InlineData(2)]
    public void Majority_survives_process_failures(int failures)
    {
        var s = new Simulation(); s.Advance(); var old = s.Leader.Status().NodeId;
        s.Down.Add(old); if (failures == 2) s.Down.Add(old == 5 ? 4 : 5);
        s.Advance(); var c = s.Commit("after failure");
        Assert.True(s.Nodes.Values.Count(n => n.Result(c.CommandId) is not null) >= 3);
    }
    [Fact] public void Minority_cannot_commit_or_authoritatively_read()
    {
        var s = new Simulation(); s.Advance(); var leader = s.Leader;
        s.Partition(leader.Status().NodeId, leader.Status().NodeId == 5 ? 4 : 5);
        var c = new RaftCommand(Guid.NewGuid(), "minority"); leader.Propose(c); var read = leader.BeginRead();
        s.Advance(); Assert.Null(leader.Result(c.CommandId)); Assert.False(leader.ReadConfirmed(read));
        s.Commit("majority"); s.Blocked.Clear(); s.Advance();
        Assert.All(s.Machines.Values, m => Assert.DoesNotContain("minority", m.Values));
    }
    [Fact] public void Three_failed_nodes_prevent_confirmation_but_preserve_committed_state()
    {
        var s = new Simulation(); s.Advance(); var c = s.Commit("before"); var leader = s.Leader;
        foreach (var id in s.Nodes.Keys.Where(x => x != leader.Status().NodeId).Take(3)) s.Down.Add(id);
        var pending = new RaftCommand(Guid.NewGuid(), "pending"); leader.Propose(pending); s.Advance();
        Assert.Null(leader.Result(pending.CommandId)); Assert.NotNull(leader.Result(c.CommandId));
    }
    [Fact] public void Commands_are_idempotent_and_conflicting_ids_rejected()
    {
        var s = new Simulation(); s.Advance(); var c = s.Commit("once"); s.Leader.Propose(c); s.Advance();
        Assert.All(s.Machines.Values, m => Assert.Single(m.Values));
        Assert.Throws<RaftConflictException>(() => s.Leader.Propose(c with { Payload = "different" }));
    }
    [Fact] public void Read_requires_fresh_majority_exchange()
    {
        var s = new Simulation(); s.Advance(); s.Commit("read"); var leader = s.Leader;
        var read = leader.BeginRead(); Assert.False(leader.ReadConfirmed(read)); s.Advance(100); Assert.True(leader.ReadConfirmed(read));
    }
    [Fact] public void Snapshot_installs_on_lagging_node_and_survives_full_restart()
    {
        var s = new Simulation(4); s.Advance(); s.Down.Add(5);
        for (var i = 0; i < 12; i++) s.Commit(i.ToString());
        s.Restart(5); s.Advance(); Assert.Equal(s.Machines[1].Values, s.Machines[5].Values);
        Assert.True(s.Nodes[5].Status().SnapshotIndex > 0);
        foreach (var id in s.Nodes.Keys.ToArray()) s.Restart(id); s.Advance(); s.Commit("restart");
        Assert.All(s.Machines.Values, m => Assert.Equal(13, m.Values.Count));
    }
    [Fact] public void Earlier_term_entry_requires_current_term_entry_to_commit()
    {
        var s = new Simulation(); var follower = s.Nodes[2];
        follower.AppendEntries(new(1, 1, 0, 0, [new(1, 1, new(Guid.NewGuid(), "older"))], 0));
        Assert.Equal(0, follower.CommitIndex); Assert.Empty(s.Machines[2].Values);
        s.Nodes[3].AppendEntries(new(1, 1, 0, 0, [new(1, 1, new(Guid.NewGuid(), "older"))], 0));
        s.Down.Add(1); s.Advance(); Assert.True(s.Leader.CommitIndex >= 2);
    }
    [Fact] public void Sqlite_persists_state_and_rejects_second_owner_or_wrong_membership()
    {
        var path = Path.Combine(Path.GetTempPath(), "raft-test-" + Guid.NewGuid(), "node.db");
        var config = new RaftOptions(1,"cluster",Enumerable.Range(1,5).ToDictionary(x=>x,x=>$"n{x}"));
        using (var db = new SqliteRaftStorage(path))
        {
            var core = new RaftCore(config,db,new TestMachine()); Assert.True(core.RequestVote(new(9,2,0,0)).Granted);
            Assert.Throws<IOException>(() => new SqliteRaftStorage(path));
        }
        using (var db = new SqliteRaftStorage(path))
        {
            var core = new RaftCore(config,db,new TestMachine()); Assert.Equal(9,core.Term);
            Assert.False(core.RequestVote(new(9,3,0,0)).Granted);
            Assert.Throws<InvalidDataException>(() => new RaftCore(config with { ClusterId="other" },db,new TestMachine()));
        }
        Directory.Delete(Path.GetDirectoryName(path)!,true);
    }
    [Theory] [InlineData(11)] [InlineData(37)] [InlineData(123)]
    public void Reordered_duplicated_and_lost_messages_preserve_applied_prefix(int seed)
    {
        var s = new Simulation(16); var random = new Random(seed); s.Advance();
        for (var iteration=0;iteration<300;iteration++)
        {
            s.Now += 20; foreach (var n in s.Nodes.Values) n.Tick(s.Now);
            if (iteration%13==0 && s.Nodes.Values.FirstOrDefault(n=>n.Role==RaftRole.Leader) is {} leader)
                leader.Propose(new(Guid.NewGuid(),$"command-{iteration}"));
            s.Collect(); var budget=20;
            while(s.Queue.Count>0 && budget-->0)
            {
                var at=random.Next(s.Queue.Count);
                if(random.Next(7)==0) s.Queue.RemoveAt(at); else s.Deliver(at,random.Next(4)==0);
            }
            var longest=s.Machines.Values.MaxBy(m=>m.Values.Count)!.Values;
            Assert.All(s.Machines.Values,m=>Assert.Equal(longest.Take(m.Values.Count),m.Values));
            Assert.All(s.Nodes.Values,n=>Assert.True(n.Status().LastApplied<=n.CommitIndex));
        }
        s.Advance(); Assert.All(s.Machines.Values,m=>Assert.Equal(s.Machines[1].Values,m.Values));
    }
}
