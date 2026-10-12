using Burmalda.ConcurrencyLab.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Burmalda.Domain.Tests;

public sealed class DatabaseLabTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"burmalda-lab-{Guid.NewGuid()}.db");
    private readonly DatabaseLabStore store;
    public DatabaseLabTests()
    {
        var options = new DbContextOptionsBuilder<LabDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        store = new(new Factory(options), NullLogger<DatabaseLabStore>.Instance);
    }

    [Fact]
    public async Task Lock_allows_exactly_one_of_two_clients_to_reserve_the_copy()
    {
        var run = await store.Create(new(DatabaseLabMode.Lock), default);
        await Task.WhenAll(run.Reserve(new("Client 1"), default), run.Reserve(new("Client 2"), default));
        var result = await run.Snapshot();
        Assert.Equal("Correct", result.Status);
        Assert.Equal(1, result.After.Loans); Assert.False(result.After.CopyAvailable);
        Assert.Single(result.Clients, x => x.Result == "SUCCESS");
        Assert.Single(result.Clients, x => x.Result == "FAIL");
        var ordered = result.Clients.OrderBy(x => x.EnteredAtUtc).ToArray();
        Assert.True(ordered[0].ExitedAtUtc <= ordered[1].EnteredAtUtc);
        Assert.Equal(2, result.Events.Count(x => x.Phase == "LOCK"));
        Assert.Equal(2, result.Events.Count(x => x.Phase == "UNLOCK"));
        await Assert.ThrowsAsync<LabConflictException>(() => run.Reserve(new("Client 3"), default));
    }

    [Fact]
    public async Task Unsafe_check_then_write_creates_two_loans_for_one_copy()
    {
        var run = await store.Create(new(DatabaseLabMode.Unsafe), default);
        await Task.WhenAll(run.Reserve(new("Client 1"), default), run.Reserve(new("Client 2"), default));
        var result = await run.Snapshot();
        Assert.Equal(2, result.After.Loans);
        Assert.All(result.Clients, x => Assert.Equal("SUCCESS", x.Result));
        Assert.Equal("Incorrect", result.Status);
    }

    [Theory]
    [InlineData(DatabaseLabMode.NoTransaction, LabFailure.None, "Saved", 1, false)]
    [InlineData(DatabaseLabMode.NoTransaction, LabFailure.AfterOperation1, "Partial", 1, true)]
    [InlineData(DatabaseLabMode.Transaction, LabFailure.None, "Committed", 1, false)]
    [InlineData(DatabaseLabMode.Transaction, LabFailure.AfterOperation1, "RolledBack", 0, true)]
    [InlineData(DatabaseLabMode.Transaction, LabFailure.BeforeCommit, "RolledBack", 0, true)]
    public async Task Persisted_state_matches_transaction_outcome(DatabaseLabMode mode, LabFailure failure, string status, int loans, bool available)
    {
        var run = await store.Create(new(mode, failure), default);
        await run.Execute(default);
        var persisted = await run.Snapshot();
        Assert.Equal(status, persisted.Status);
        Assert.Equal(loans, persisted.After.Loans);
        Assert.Equal(available, persisted.After.CopyAvailable);
        if (mode == DatabaseLabMode.Transaction)
            Assert.Contains(persisted.Events, x => x.Phase == (failure == LabFailure.None ? "COMMIT" : "ROLLBACK"));
        await Assert.ThrowsAsync<LabConflictException>(() => run.Execute(default));
    }

    [Theory]
    [InlineData(LabFailure.None, 3, "Completed", 1, false, 10)]
    [InlineData(LabFailure.Points, 3, "Compensated", 0, true, 0)]
    [InlineData(LabFailure.Loan, 5, "Compensated", 0, true, 0)]
    public async Task Saga_commits_steps_and_compensates_in_reverse_order(LabFailure failure, int steps, string status, int loans, bool available, int points)
    {
        var run = await store.Create(new(DatabaseLabMode.Saga, failure), default);
        var first = await run.Step(default);
        Assert.False(first.After.CopyAvailable); Assert.Equal(0, first.After.Loans);
        for (var i = 1; i < steps; i++) await run.Step(default);
        var result = await run.Snapshot();
        Assert.Equal(status, result.Status); Assert.Equal(loans, result.After.Loans);
        Assert.Equal(available, result.After.CopyAvailable); Assert.Equal(points, result.After.ReservedPoints);
        if (failure == LabFailure.Loan)
            Assert.Equal(new[] { "ReleasePoints", "ReleaseBookCopy" }, result.Events.TakeLast(2).Select(x => x.Phase));
        await Assert.ThrowsAsync<LabConflictException>(() => run.Step(default));
    }

    [Fact]
    public async Task Unknown_run_is_reported_without_mutating_database()
    {
        await store.Create(new(DatabaseLabMode.Lock), default);
        Assert.Throws<LabNotFoundException>(() => store.Get(Guid.NewGuid()));
    }

    [Fact]
    public async Task Cancelled_transaction_rolls_back_the_first_saved_operation()
    {
        var run = await store.Create(new(DatabaseLabMode.Transaction), default);
        using var cancellation = new CancellationTokenSource();
        var execution = run.Execute(cancellation.Token);
        for (var i = 0; i < 100 && !(await run.Snapshot()).Events.Any(x => x.Phase == "OPERATION 1"); i++)
            await Task.Delay(5);
        cancellation.Cancel();
        var result = await execution;
        Assert.Equal("RolledBack", result.Status); Assert.Equal(0, result.After.Loans); Assert.True(result.After.CopyAvailable);
    }

    [Fact]
    public async Task Database_enforces_nonnegative_points_and_existing_copy_foreign_key()
    {
        var run = await store.Create(new(DatabaseLabMode.Saga), default);
        var options = new DbContextOptionsBuilder<LabDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        await using (var db = new LabDbContext(options))
        {
            var copy = await db.Copies.SingleAsync(x => x.Id == run.Id); copy.ReservedPoints = -1;
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using (var db = new LabDbContext(options))
        {
            db.Loans.Add(new() { CopyId = Guid.NewGuid(), ClientId = "Client 1" });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        var result = await run.Snapshot(); Assert.Equal(0, result.After.ReservedPoints); Assert.Equal(0, result.After.Loans);
    }

    public void Dispose() { if (File.Exists(path)) File.Delete(path); }
    private sealed class Factory(DbContextOptions<LabDbContext> options) : IDbContextFactory<LabDbContext>
    {
        public LabDbContext CreateDbContext() => new(options);
    }
}
