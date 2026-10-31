using Burmalda.LibraryGame;
using Burmalda.LibraryGame.Api;
using Burmalda.LibraryGame.Subscribers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Burmalda.LibraryGame.Tests;

public sealed class SubscriberTests
{
    private sealed class Clients : IHttpClientFactory { public HttpClient CreateClient(string name)=>new(); }
    private static GameEvent Event(Guid user,Guid session,string type="BookShelved",bool demo=true)=>new(Guid.NewGuid(),type,"0:1",DateTimeOffset.UtcNow,user,session,demo,"unit",Guid.NewGuid(),1,new("0:1",0,1,1));
    [Fact] public async Task DuplicateAchievementAndAnalyticsDeliveriesDoNotDuplicateResults()
    {
        await using var f=new TestDatabase<SubscriberDb>();await f.Initialize();var h=new SubscriberHandlers(f,new Clients(),new ConfigurationBuilder().Build(),NullLogger<SubscriberHandlers>.Instance);
        var e=Event(Guid.NewGuid(),Guid.NewGuid());
        await h.Handle("AchievementTracker",e,default);await h.Handle("AchievementTracker",e,default);
        await h.Handle("AnalyticsSubscriber",e,default);await h.Handle("AnalyticsSubscriber",e,default);
        await using var db=f.CreateDbContext();Assert.Equal(1,await db.Achievements.CountAsync());Assert.Equal(1,await db.Outgoing.CountAsync());Assert.Equal(1,(await db.Analytics.SingleAsync()).Count);
    }
    [Fact] public async Task DelayedSubscriberDoesNotHoldOtherSubscribersTransaction()
    {
        await using var f=new TestDatabase<SubscriberDb>();await f.Initialize();var h=new SubscriberHandlers(f,new Clients(),new ConfigurationBuilder().Build(),NullLogger<SubscriberHandlers>.Instance);
        var e=Event(Guid.NewGuid(),Guid.NewGuid());await h.Control(new(e.SessionId,"AchievementTracker",new(DelayMs:500)),default);
        var slow=h.Handle("AchievementTracker",e,default);await Task.Delay(40);
        await h.Handle("EventLogger",e,default);Assert.False(slow.IsCompleted);await slow;
    }
    [Fact] public async Task FailureControlsAreScopedToDemoSessionAndDoNotAffectOthers()
    {
        await using var f=new TestDatabase<SubscriberDb>();await f.Initialize();var h=new SubscriberHandlers(f,new Clients(),new ConfigurationBuilder().Build(),NullLogger<SubscriberHandlers>.Instance);
        var e=Event(Guid.NewGuid(),Guid.NewGuid());await h.Control(new(e.SessionId,"EventLogger",new(Available:false)),default);
        await Assert.ThrowsAsync<GameRuleException>(()=>h.Handle("EventLogger",e,default));
        await h.Handle("EventLogger",e with {EventId=Guid.NewGuid(),Demo=false},default);
        await h.Handle("EventLogger",Event(e.UserId,Guid.NewGuid()),default);
    }
    [Fact] public async Task CommandReceiptsAndEffectArePersistentlyIdempotent()
    {
        await using var f=new TestDatabase<GameDb>();await f.Initialize();var store=new GameStore(f,NullLogger<GameStore>.Instance);var user=Guid.NewGuid();var state=await store.Current(user,false,default);
        var cmd=new GameCommand(Guid.NewGuid(),state.Version,"PickBook","0:1");var first=await store.Execute(user,false,cmd,default);var replay=await store.Execute(user,false,cmd,default);Assert.Equal(first.Version,replay.Version);
        await Assert.ThrowsAsync<GameRuleException>(()=>store.Execute(user,false,cmd with {BookId="0:2"},default));
        await using var db=f.CreateDbContext();Assert.Equal(1,await db.Outgoing.CountAsync());Assert.Equal(1,await db.Commands.CountAsync());
        var other=await store.Current(Guid.NewGuid(),false,default);Assert.Empty(other.Held);
    }
}
