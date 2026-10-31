using System.Net;
using Burmalda.LibraryGame;
using Burmalda.LibraryGame.Broker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Burmalda.LibraryGame.Tests;

public sealed class TestDatabase<T> : IDbContextFactory<T>, IAsyncDisposable where T : DbContext
{
    private readonly string file = Path.Combine(Path.GetTempPath(), $"library-test-{Guid.NewGuid():N}.db");
    public T CreateDbContext() => (T)Activator.CreateInstance(typeof(T), new DbContextOptionsBuilder<T>().UseSqlite($"Data Source={file};Pooling=False").Options)!;
    public async Task Initialize() { await using var db = CreateDbContext(); await db.Database.EnsureCreatedAsync(); }
    public ValueTask DisposeAsync() { File.Delete(file); return ValueTask.CompletedTask; }
}
public sealed class BrokerTests
{
    private static BrokerStore Store(TestDatabase<BrokerDb> f, int capacity = 10000) => new(f, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Broker:Capacity"] = capacity.ToString() }).Build(), NullLogger<BrokerStore>.Instance);
    private static GameEvent Event(string type = "BookShelved", Guid? session = null) => new(Guid.NewGuid(),type,"0:1",DateTimeOffset.UtcNow,Guid.NewGuid(),session ?? Guid.NewGuid(),true,"test",Guid.NewGuid(),1,new("0:1",0,1,1));
    private static SubscriberRegistration Sub(string id, params string[] types) => new(id,types,$"http://localhost:5111/internal/subscribers/{id}");
    [Fact] public async Task FiltersFanoutAndPublicationDeduplication()
    {
        await using var db = new TestDatabase<BrokerDb>(); await db.Initialize(); var store = Store(db);
        await store.Subscribe(Sub("EventLogger","BookShelved","AbilityUsed")); await store.Subscribe(Sub("AchievementTracker","BookShelved")); await store.Subscribe(Sub("AbilityHandler","AbilityUsed"));
        var e = Event(); Assert.Equal(2,(await store.Publish(e)).Recipients); Assert.True((await store.Publish(e)).Duplicate);
        var snapshot = await store.Snapshot(e.SessionId,default); Assert.Equal(2,snapshot.Deliveries.Count); Assert.DoesNotContain(snapshot.Deliveries,x => x.Subscriber == "AbilityHandler");
    }
    [Fact] public async Task NewSubscriberOnlyReceivesFutureEvents()
    {
        await using var db = new TestDatabase<BrokerDb>(); await db.Initialize(); var store=Store(db); var first=Event();
        await store.Publish(first); await store.Subscribe(Sub("AnalyticsSubscriber","BookShelved"));
        Assert.Empty((await store.Snapshot(first.SessionId,default)).Deliveries);
        var second=Event(session:first.SessionId); Assert.Equal(1,(await store.Publish(second)).Recipients);
    }
    [Fact] public async Task CapacityRejectsWholePublicationWithoutPartialWrite()
    {
        await using var db=new TestDatabase<BrokerDb>(); await db.Initialize(); var store=Store(db,1);
        await store.Subscribe(Sub("EventLogger","BookShelved")); await store.Subscribe(Sub("AchievementTracker","BookShelved")); var e=Event();
        var error=await Assert.ThrowsAsync<GameRuleException>(()=>store.Publish(e)); Assert.Equal(503,error.Status);
        await using var check=db.CreateDbContext(); Assert.Empty(await check.Events.ToListAsync()); Assert.Empty(await check.Deliveries.ToListAsync());
    }
    [Fact] public async Task FailureDoesNotBlockOtherSubscriberAndRetriesReachDeadLetter()
    {
        await using var db=new TestDatabase<BrokerDb>(); await db.Initialize(); var store=Store(db);
        await store.Subscribe(Sub("EventLogger","BookShelved")); await store.Subscribe(Sub("AchievementTracker","BookShelved")); var e=Event(); await store.Publish(e);
        var claimed=await store.Claim(default); Assert.Equal(2,claimed.Count);
        var logger=claimed.Single(x=>x.Row.Subscriber=="EventLogger"); await store.Finish(logger.Row.Id,e,null,default);
        var broken=claimed.Single(x=>x.Row.Subscriber=="AchievementTracker");
        for(var attempt=1;attempt<=5;attempt++) {
            await store.Finish(broken.Row.Id,e,"UNAVAILABLE",default);
            if(attempt<5) { await using var adjust=db.CreateDbContext(); await adjust.Deliveries.Where(x=>x.Id==broken.Row.Id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.NextAt,0L)); broken=Assert.Single(await store.Claim(default)); }
        }
        var snapshot=await store.Snapshot(e.SessionId,default); Assert.Contains(snapshot.Deliveries,x=>x.Subscriber=="EventLogger"&&x.Status=="Success"); Assert.Contains(snapshot.Deliveries,x=>x.Status=="DeadLetter"&&x.Attempts==5);
        Assert.Equal(1,await store.Replay(e.SessionId,default)); Assert.Single(await store.Claim(default));
    }
    [Fact] public async Task RestartRecoversInFlightAndPreservesSubscriptionAndOrder()
    {
        await using var db=new TestDatabase<BrokerDb>(); await db.Initialize(); var store=Store(db); await store.Subscribe(Sub("EventLogger","BookShelved"));
        var e=Event(); await store.Publish(e); await store.Publish(Event(session:e.SessionId)); Assert.Single(await store.Claim(default)); Assert.Empty(await store.Claim(default));
        var restarted=Store(db); await restarted.Recover(default); var recovered=Assert.Single(await restarted.Claim(default)); Assert.Equal(e.EventId,recovered.Event.EventId);
        await restarted.Finish(recovered.Row.Id,e,null,default); Assert.Single(await restarted.Claim(default));
    }
    [Fact] public async Task ArbitraryCallbackUrlIsRejected()
    {
        await using var db=new TestDatabase<BrokerDb>(); await db.Initialize(); await Assert.ThrowsAsync<GameRuleException>(()=>Store(db).Subscribe(new("EventLogger",["BookShelved"],"http://example.com/")));
    }
}
