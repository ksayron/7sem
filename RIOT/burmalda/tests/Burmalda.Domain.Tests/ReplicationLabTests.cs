using Burmalda.ConcurrencyLab.Api;
using Microsoft.Extensions.Logging.Abstractions;

namespace Burmalda.Domain.Tests;

public sealed class ReplicationLabTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "burmalda-streaming-" + Guid.NewGuid());
    private readonly TestCluster cluster = new();
    private ReplicationLabStore store;
    public ReplicationLabTests() => store = Open();
    private ReplicationLabStore Open() => new(root, cluster, NullLogger<ReplicationLabStore>.Instance, catchupTimeout: TimeSpan.FromMilliseconds(500));
    private static async Task<ReplicationSnapshot> Wait(ReplicationLabStore store, Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var state = await store.Snapshot(id, timeout.Token);
            if (!state.ClusterBusy && state.ActionStatus != "Running") return state;
            await Task.Delay(10, timeout.Token);
        }
    }
    private CopyAvailabilityRequest Change(long version, bool available = false) => new("Client 1", cluster.CopyId, available, version);

    [Fact]
    public async Task Sessions_observe_existing_database_and_never_initialize_inventory()
    {
        var first = await store.Create(); var second = await store.Create();
        Assert.True(first.Consistent); Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(cluster.CopyId, Assert.Single(first.Nodes[0].Copies).Id);
        Assert.Equal(0, cluster.Updates);
        await store.Update(first.Id, Change(7), default);
        Assert.Equal("Unavailable", (await store.Snapshot(second.Id)).Nodes[0].Copies[0].Status);
    }
    [Fact]
    public async Task Committed_update_and_replay_are_separate_and_versions_prevent_lost_updates()
    {
        var run = await store.Create(); await store.Configure(run.Id, new(false), default);
        var updated = await store.Update(run.Id, Change(7), default);
        Assert.True(updated.Operations.Last().Committed); Assert.Equal("PENDING", updated.Operations.Last().Replication);
        Assert.False(updated.Consistent);
        var ex = await Assert.ThrowsAsync<ReplicationLabException>(() => store.Update(run.Id, Change(7), default));
        Assert.Equal(409, ex.Status); Assert.Equal(1, cluster.Updates);
        await store.Configure(run.Id, new(true), default);
        Assert.True((await store.Snapshot(run.Id)).Consistent);
    }
    [Fact]
    public async Task Paused_stale_standby_cannot_be_promoted_and_source_can_be_restored()
    {
        var run = await store.Create(); await store.Configure(run.Id, new(false), default);
        await store.Update(run.Id, Change(7), default);
        store.StartAction(run.Id, "stop", "postgres"); await Wait(store, run.Id);
        var ex = await Assert.ThrowsAsync<ReplicationLabException>(() => store.Read(run.Id, new("Client 2"), default));
        Assert.Equal(503, ex.Status); Assert.DoesNotContain("promote", cluster.Calls);
        store.StartAction(run.Id, "recover", "postgres");
        Assert.Equal("Completed", (await Wait(store, run.Id)).ActionStatus);
        Assert.Equal("postgres", (await store.Snapshot(run.Id)).ActiveInstance);
    }
    [Fact]
    public async Task Unplanned_loss_without_durable_fencing_boundary_fails_closed()
    {
        var run = await store.Create(); await cluster.Stop("postgres", default);
        var ex = await Assert.ThrowsAsync<ReplicationLabException>(() => store.Read(run.Id, new("Client"), default));
        Assert.Equal(503, ex.Status); Assert.DoesNotContain("promote", cluster.Calls);
    }
    [Fact]
    public async Task Promotion_requires_verified_stopped_source()
    {
        var run = await store.Create(); store.StartAction(run.Id, "stop", "postgres"); await Wait(store, run.Id);
        cluster.RefuseFenceProof = true;
        var ex = await Assert.ThrowsAsync<ReplicationLabException>(() => store.Read(run.Id, new("Client"), default));
        Assert.Equal(503, ex.Status); Assert.DoesNotContain("promote", cluster.Calls);
    }
    [Fact]
    public async Task Two_cycles_reverse_roles_and_restore_measured_availability()
    {
        var run = await store.Create();
        foreach (var expected in new[] { "postgres-replica", "postgres" })
        {
            store.StartAction(run.Id, "cycle", copy: cluster.CopyId);
            var result = await Wait(store, run.Id);
            Assert.Equal("Completed", result.ActionStatus); Assert.Null(result.ActionError);
            Assert.Equal(expected, result.ActiveInstance); Assert.True(result.Consistent);
            Assert.All(result.Nodes, n => Assert.Equal("Available", n.Copies[0].Status));
        }
        Assert.Equal(12, cluster.Updates); Assert.Equal(2, cluster.Calls.Count(x => x == "recover"));
    }
    [Fact]
    public async Task Recovery_failure_preserves_authority_and_can_resume_after_coordinator_restart()
    {
        var run = await store.Create(); store.StartAction(run.Id, "stop", "postgres"); await Wait(store, run.Id);
        await store.MaintainWriter(default);
        await store.Read(run.Id, new("Client"), default);
        cluster.FailRecovery = true; store.StartAction(run.Id, "recover", "postgres");
        Assert.Equal("Failed", (await Wait(store, run.Id)).ActionStatus);
        store.Dispose(); store = Open(); cluster.FailRecovery = false;
        store.StartAction(run.Id, "recover", "postgres"); var result = await Wait(store, run.Id);
        Assert.Equal("Completed", result.ActionStatus); Assert.Equal("postgres-replica", result.ActiveInstance); Assert.True(result.Consistent);
    }
    [Fact]
    public async Task Both_nodes_unavailable_reject_requests_without_mutating_data()
    {
        var run = await store.Create(); await cluster.Stop("postgres", default); await cluster.Stop("postgres-replica", default);
        var ex = await Assert.ThrowsAsync<ReplicationLabException>(() => store.Update(run.Id, Change(7), default));
        Assert.Equal(503, ex.Status); Assert.Equal(0, cluster.Updates);
    }
    [Fact]
    public async Task Unknown_commit_outcome_is_recorded_without_retry()
    {
        var run = await store.Create(); cluster.UnknownCommit = true;
        await Assert.ThrowsAsync<ReplicationLabException>(() => store.Update(run.Id, Change(7), default));
        var state = await store.Snapshot(run.Id);
        Assert.Null(state.Operations.Last().Committed); Assert.Equal(1, cluster.Updates);
    }
    [Fact]
    public async Task Cluster_gate_is_shared_across_sessions()
    {
        var a = await store.Create(); var b = await store.Create();
        cluster.StopBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        store.StartAction(a.Id, "stop", "postgres-replica");
        var ex = await Assert.ThrowsAsync<ReplicationLabException>(() => store.Update(b.Id, Change(7), default));
        Assert.Equal(409, ex.Status);
        cluster.StopBarrier.SetResult(); await Wait(store, a.Id);
    }
    [Fact]
    public void Second_coordinator_cannot_open_same_journal_root() => Assert.Throws<IOException>(() => Open());
    [Fact]
    public async Task Background_failover_promotes_without_a_lab_read_or_update()
    {
        var run = await store.Create();
        store.StartAction(run.Id, "stop", "postgres"); await Wait(store, run.Id);
        await Task.WhenAll(store.MaintainWriter(default), store.MaintainWriter(default));
        var result = await store.Snapshot(run.Id);
        Assert.Equal("postgres-replica", result.ActiveInstance);
        Assert.Single(cluster.Calls, x => x == "promote");
        Assert.Contains(result.Events, e => e.Operation == "Failover" && e.Result == "SUCCESS");
        Assert.Empty(result.Operations); Assert.Equal(0, cluster.Updates);
    }
    [Fact]
    public async Task Background_failover_resumes_from_durable_authority_after_restart()
    {
        var run = await store.Create();
        store.StartAction(run.Id, "stop", "postgres"); await Wait(store, run.Id);
        store.Dispose(); store = Open();
        await store.MaintainWriter(default);
        Assert.Equal("postgres-replica", (await store.Snapshot(run.Id)).ActiveInstance);
    }
    [Fact]
    public async Task Background_monitor_does_not_promote_unplanned_or_unfenced_loss()
    {
        await store.Create(); await cluster.Stop("postgres", default);
        await store.MaintainWriter(default);
        Assert.DoesNotContain("promote", cluster.Calls);
    }
    [Fact]
    public async Task Background_failover_preserves_replay_and_fencing_guards()
    {
        var run = await store.Create(); await store.Configure(run.Id, new(false), default);
        store.StartAction(run.Id, "stop", "postgres"); await Wait(store, run.Id);
        await Assert.ThrowsAsync<ReplicationLabException>(() => store.MaintainWriter(default));
        await store.Configure(run.Id, new(true), default);
        cluster.RefuseFenceProof = true;
        await Assert.ThrowsAsync<ReplicationLabException>(() => store.MaintainWriter(default));
        Assert.DoesNotContain("promote", cluster.Calls);
        cluster.RefuseFenceProof = false; await store.MaintainWriter(default);
        Assert.Equal("postgres-replica", (await store.Snapshot(run.Id)).ActiveInstance);
    }
    [Fact]
    public async Task Monitor_does_not_interrupt_an_operator_action()
    {
        var run = await store.Create();
        cluster.StopBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        store.StartAction(run.Id, "stop", "postgres");
        await store.MaintainWriter(default);
        Assert.DoesNotContain("promote", cluster.Calls);
        cluster.StopBarrier.SetResult(); await Wait(store, run.Id);
        await store.MaintainWriter(default);
        Assert.Single(cluster.Calls, x => x == "promote");
    }
    [Theory]
    [InlineData("", 400)] [InlineData("bogus-node", 400)]
    public void Docker_node_arguments_are_allowlisted(string node, int status)
    { Assert.Equal(status, Assert.Throws<ReplicationLabException>(() => PostgresReplicationCluster.CheckNode(node)).Status); }
    [Fact]
    public async Task Invalid_input_and_empty_cycles_do_not_modify_database()
    {
        var run = await store.Create();
        await Assert.ThrowsAsync<ReplicationLabException>(() => store.Update(run.Id, new("", Guid.Empty, false, -1), default));
        store.StartAction(run.Id, "cycle", copy: Guid.NewGuid());
        Assert.Equal("Failed", (await Wait(store, run.Id)).ActionStatus); Assert.Equal(0, cluster.Updates);
    }
    [Theory]
    [InlineData("1/00000000", "0/FFFFFFFF", true)]
    [InlineData("0/10", "0/F", true)]
    [InlineData("0/F", "0/10", false)]
    public void Lsn_comparison_is_numeric_across_segments(string replay, string required, bool expected)
        => Assert.Equal(expected, ReplicationLabStore.Replayed(replay, required));
    public void Dispose() { store.Dispose(); Directory.Delete(root, true); }

    private sealed class TestCluster : IReplicationCluster
    {
        public Guid CopyId { get; } = Guid.NewGuid();
        private readonly Guid bookId = Guid.NewGuid();
        private readonly Dictionary<string, ReplicationNode> nodes;
        private ulong lsn = 0x100;
        public int Updates { get; private set; }
        public List<string> Calls { get; } = [];
        public bool FailRecovery { get; set; }
        public bool RefuseFenceProof { get; set; }
        public bool UnknownCommit { get; set; }
        public TaskCompletionSource? StopBarrier { get; set; }
        public TestCluster()
        {
            ReplicationBook[] books = [new(bookId, "Existing catalog row", 1)];
            ReplicationCopy[] copies = [new(CopyId, bookId, "Existing catalog row", "Available", false, 7)];
            nodes = new()
            {
                ["postgres"] = new("postgres", "AVAILABLE", "Primary", false, false, "0/100", null, false, "streaming", DateTimeOffset.UtcNow, books, copies, null),
                ["postgres-replica"] = new("postgres-replica", "AVAILABLE", "Standby", true, false, null, "0/100", false, "streaming", DateTimeOffset.UtcNow, books, copies, null)
            };
        }
        public Task<ReplicationNode> Observe(string node, CancellationToken ct) => nodes[node].State == "AVAILABLE"
            ? Task.FromResult(nodes[node]) : throw new IOException("offline");
        private void Replicate()
        {
            var writer = nodes.Values.SingleOrDefault(n => n.Role == "Primary" && !n.ReadOnly && n.State == "AVAILABLE");
            var standby = nodes.Values.SingleOrDefault(n => n.Role == "Standby" && n.State == "AVAILABLE" && !n.ReplayPaused);
            if (writer is not null && standby is not null) nodes[standby.Id] = standby with { ReplayLsn = writer.WalLsn, Copies = writer.Copies, Books = writer.Books };
        }
        public Task<CopyCommit> Update(string node, CopyAvailabilityRequest input, CancellationToken ct)
        {
            var n = nodes[node]; var c = n.Copies.Single();
            if (c.Version != input.ExpectedVersion) throw new ReplicationLabException(409, "stale version");
            Updates++; lsn += 16; var wal = $"0/{lsn:X}";
            nodes[node] = n with { WalLsn = wal, Copies = [c with { Version = c.Version + 1, Status = input.Available ? "Available" : "Unavailable" }], Books = [n.Books[0] with { Available = input.Available ? 1 : 0 }] };
            Replicate();
            if (UnknownCommit) throw new ReplicationLabException(503, "Исход COMMIT неизвестен.");
            return Task.FromResult(new CopyCommit(CopyId, bookId, c.Version + 1, wal));
        }
        public Task<string> QuiesceAndFence(string node, CancellationToken ct)
        { nodes[node] = nodes[node] with { Fenced = true, ReadOnly = true }; return Task.FromResult(nodes[node].WalLsn!); }
        public async Task Stop(string node, CancellationToken ct)
        { if (StopBarrier is not null) await StopBarrier.Task; nodes[node] = nodes[node] with { State = "UNAVAILABLE" }; }
        public Task<bool> IsStopped(string node, CancellationToken ct) => Task.FromResult(!RefuseFenceProof && nodes[node].State != "AVAILABLE");
        public Task Promote(string node, CancellationToken ct)
        { Calls.Add("promote"); nodes[node] = nodes[node] with { Role = "Primary", ReadOnly = false, WalLsn = nodes[node].ReplayLsn }; return Task.CompletedTask; }
        public Task Recover(string node, string source, CancellationToken ct)
        {
            Calls.Add("recover"); if (FailRecovery) throw new ReplicationLabException(503, "rewind interrupted");
            nodes[node] = nodes[node] with { Role = "Standby", State = "AVAILABLE", ReadOnly = true, Fenced = false, ReplayPaused = false, WalLsn = null, ReplayLsn = nodes[source].WalLsn, Books = nodes[source].Books, Copies = nodes[source].Copies };
            return Task.CompletedTask;
        }
        public Task RestoreSource(string node, CancellationToken ct)
        { nodes[node] = nodes[node] with { Fenced = false, ReadOnly = false, State = "AVAILABLE" }; return Task.CompletedTask; }
        public Task Start(string node, CancellationToken ct) { nodes[node] = nodes[node] with { State = "AVAILABLE" }; Replicate(); return Task.CompletedTask; }
        public Task Pause(string node, bool paused, CancellationToken ct)
        { nodes[node] = nodes[node] with { ReplayPaused = paused }; if (!paused) Replicate(); return Task.CompletedTask; }
    }
}
