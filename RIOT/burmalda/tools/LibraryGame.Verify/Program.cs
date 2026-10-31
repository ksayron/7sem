using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Burmalda.LibraryGame;

var root = Path.GetFullPath(args.FirstOrDefault() ?? ".");
var output = Path.Combine(root, "lab-results", "pubsub-verification"); Directory.CreateDirectory(output);
var checks = new List<string>();
using var http = new HttpClient { BaseAddress = new("http://localhost:5000"), Timeout = TimeSpan.FromSeconds(20) };
using var internalHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
internalHttp.DefaultRequestHeaders.Add("X-Library-Key", Environment.GetEnvironmentVariable("Library__InternalKey") ?? "library-local-only-change-me");
void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks.Add(name); Console.WriteLine($"PASS {name}"); }
async Task<T> Get<T>(string path) => (await http.GetFromJsonAsync<T>(path))!;
async Task<T> Post<T>(string path, object? body = null) { using var r = await http.PostAsJsonAsync(path, body); if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"{path}: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}"); var text=await r.Content.ReadAsStringAsync();return text.Length==0?default!:GameJson.Read<T>(text); }
async Task Until(Func<Task<bool>> test, string name, int seconds = 90) { var watch=Stopwatch.StartNew();while(watch.Elapsed.TotalSeconds<seconds){if(await test()){Check(true,name);return;}await Task.Delay(250);}throw new TimeoutException(name); }
async Task<BrokerSnapshot> Broker(string id,Guid session) => (await internalHttp.GetFromJsonAsync<BrokerSnapshot>($"http://localhost:{(id=="A"?5109:5110)}/internal/snapshot/{session}"))!;
async Task Component(string name,string action) {
    var info=new ProcessStartInfo("pwsh"){UseShellExecute=false,CreateNoWindow=true};
    foreach(var arg in new[]{"-NoProfile","-File",Path.Combine(root,"tools","Set-PubSubComponent.ps1"),"-Component",name,"-Action",action})info.ArgumentList.Add(arg);
    using var process=Process.Start(info)!;await process.WaitForExitAsync();if(process.ExitCode!=0)throw new InvalidOperationException($"{name} {action} failed");
    if(action!="Stop") await Until(async()=>{try{using var r=await internalHttp.GetAsync($"http://localhost:{(name=="BrokerA"?5109:name=="BrokerB"?5110:name=="GameApi"?5108:5111)}/health/ready");return r.IsSuccessStatusCode;}catch(HttpRequestException){return false;}},$"{name} ready",30);
}
var demo = true;
GameState state = null!;
async Task Refresh() => state=(await Get<GameSnapshot>($"/api/library-game/state?demo={demo}")).State;
async Task<GameState> Command(string type,string? book=null,int? series=null,string? upgrade=null) {
    state=await Post<GameState>($"/api/library-game/commands?demo={demo}",new GameCommand(Guid.NewGuid(),state.Version,type,book,series,upgrade,"csharp-verifier"));return state;
}
async Task Sort() {
    if(state.Held.Count==0){var id=Enumerable.Range(0,state.OpenSeries).SelectMany(s=>Enumerable.Range(1,12).Select(v=>LibraryWorld.BookId(s,v))).First(state.IsWorldBook);await Command("PickBook",id);}
    await Command("PlaceBook",series:GameRules.ParseBook(state.Held.Last()).Series);
}
async Task Control(string id,SubscriberControl setting)=>await Post<JsonElement>("/api/library-game/lab/control",new{subscriber=id,control=setting});
try {
    using(var unauthorized=await http.GetAsync("/api/library-game/state"))Check(unauthorized.StatusCode==HttpStatusCode.Unauthorized,"game-requires-existing-jwt");
    var email=$"lr6-{Guid.NewGuid():N}@example.invalid";var password=$"Lab6-{Guid.NewGuid():N}!";
    await Post<JsonElement>("/api/identity/register",new{email,displayName="Хранитель ЛР6",password});
    var login=await Post<JsonElement>("/api/identity/login",new{email,password});http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",login.GetProperty("accessToken").GetString());
    await Refresh();Check(state.Shelved.Count==0,"fresh-isolated-demo-save");
    var command=new GameCommand(Guid.NewGuid(),state.Version,"PickBook","0:1",ClientId:"csharp-verifier");
    var first=await Post<GameState>("/api/library-game/commands?demo=true",command);var duplicate=await Post<GameState>("/api/library-game/commands?demo=true",command);
    Check(first.Version==duplicate.Version&&duplicate.Held.Count==1,"command-retry-does-not-pick-twice");state=first;
    using(var wrong=await http.PostAsJsonAsync("/api/library-game/commands?demo=true",new GameCommand(Guid.NewGuid(),state.Version,"PlaceBook",SeriesId:1)))Check(wrong.StatusCode==HttpStatusCode.BadRequest,"wrong-shelf-rejected");
    using(var stale=await http.PostAsJsonAsync("/api/library-game/commands?demo=true",new GameCommand(Guid.NewGuid(),0,"PlaceBook",SeriesId:0)))Check(stale.StatusCode==HttpStatusCode.Conflict,"stale-version-rejected");
    await Command("PlaceBook",series:0);
    await Until(async()=>{var b=await Broker("A",state.SessionId);return b.Deliveries.Any(x=>x.EventType=="BookShelved"&&x.Subscriber=="EventLogger"&&x.Status=="Success")&&b.Deliveries.Any(x=>x.EventType=="BookShelved"&&x.Subscriber=="AchievementTracker"&&x.Status=="Success");},"one-event-fanout-and-type-filtering");
    await Until(async()=>{var s=await Get<GameSnapshot>("/api/library-game/state?demo=true");return s.Projections.Achievements.Count==1&&s.Projections.Notifications.Count>=1;},"achievement-event-crosses-second-broker");
    await Control("EventLogger",new(DelayMs:1000));await Control("AchievementTracker",new(DelayMs:3000));
    await Command("PickBook","0:2");
    await Until(async()=>(await Broker("A",state.SessionId)).Deliveries.Count(x=>x.EventType=="BookPickedUp"&&x.Subscriber=="EventLogger"&&x.Status=="Success")>=2,"previous-pickup-drained-before-delay-measurement");
    var delayedStart=DateTimeOffset.UtcNow;await Command("PlaceBook",series:0);
    await Until(async()=>{
        var b=await Broker("A",state.SessionId);var e=b.Journal.FirstOrDefault(x=>x.EventType=="BookShelved"&&x.Subscriber=="EventLogger"&&x.Result=="SUCCESS"&&x.Time>=delayedStart);
        if(e is null)return false;var starts=b.Journal.Where(x=>x.EventId==e.EventId&&x.Result.StartsWith("START")&&x.Subscriber is "EventLogger" or "AchievementTracker").ToArray();
        var end=b.Journal.FirstOrDefault(x=>x.EventId==e.EventId&&x.Subscriber=="AchievementTracker"&&x.Result=="SUCCESS");
        return starts.Length==2&&end is not null&&(starts.Max(x=>x.Time)-starts.Min(x=>x.Time)).TotalMilliseconds<800&&(end.Time-e.Time).TotalMilliseconds>1200;
    },"delays-overlap-and-have-independent-finish-times",30);
    await Control("EventLogger",new());await Control("AchievementTracker",new(Available:false));await Sort();
    await Until(async()=>{var b=await Broker("A",state.SessionId);return b.Deliveries.Any(x=>x.Subscriber=="AchievementTracker"&&x.Status=="DeadLetter")&&b.Deliveries.Count(x=>x.Subscriber=="EventLogger"&&x.EventType=="BookShelved"&&x.Status=="Success")>=3;},"subscriber-failure-deadletter-others-continue",45);
    await Control("AchievementTracker",new());await Post<JsonElement>("/api/library-game/lab/replay");
    await Until(async()=>!(await Broker("A",state.SessionId)).Deliveries.Any(x=>x.Status=="DeadLetter"),"manual-replay-after-subscriber-recovery");
    await Post<JsonElement>("/api/library-game/lab/analytics");await Sort();
    await Until(async()=> (await Get<GameSnapshot>("/api/library-game/state?demo=true")).Projections.Analytics.GetValueOrDefault("BookShelved")>=1,"new-subscriber-on-broker-b-without-publisher-change");
    while(state.Shelved.Count<6)await Sort();await Command("BuyUpgrade",upgrade:"hands");
    await Command("PickBook","1:1");await Command("UseAbility");var op=state.Operations.Last().Id;
    await Until(async()=>{await Refresh();return state.Operations.Single(x=>x.Id==op).Status=="Completed";},"ability-executed-by-separate-csharp-subscriber");Check(state.Held.All(x=>x.StartsWith("1:"))&&state.Held.Count==3,"attraction-only-matching-series-and-free-slots");
    await Component("Subscribers","Stop");try{await Sort();await Until(async()=>(await Broker("A",state.SessionId)).Deliveries.Any(x=>x.Status=="Pending"&&x.Error!=null),"real-http-subscriber-process-failure",20);}finally{await Component("Subscribers","Start");}
    await Until(async()=>!(await Broker("A",state.SessionId)).Deliveries.Any(x=>x.Status is "Pending" or "InFlight" or "DeadLetter"),"delivery-recovers-after-subscriber-process-restart");
    await Component("BrokerB","Stop");try{await Sort();await Until(async()=>(await Broker("A",state.SessionId)).Deliveries.Any(x=>x.Subscriber=="bridge-B"&&x.Error!=null),"second-broker-outage-buffers-bridge",20);}finally{await Component("BrokerB","Start");}
    await Until(async()=>!(await Broker("A",state.SessionId)).Deliveries.Any(x=>x.Status is "Pending" or "InFlight" or "DeadLetter"),"bridge-recovers-with-original-events");
    await Component("BrokerA","Stop");try{await Sort();var current=await Get<GameSnapshot>("/api/library-game/state?demo=true");Check(current.State.Shelved.Count==state.Shelved.Count,"game-commit-succeeds-while-broker-is-down");}finally{await Component("BrokerA","Start");}
    await Until(async()=>{var lab=await Get<JsonElement>("/api/library-game/lab");return lab.GetProperty("outboxPending").GetInt32()==0;},"publisher-outbox-retries-after-broker-restart");
    // Stop with an accepted event pending; its recipients and work must survive SQLite restart.
    await Control("EventLogger",new(DelayMs:3000));await Sort();
    await Until(async()=>(await Broker("A",state.SessionId)).Deliveries.Any(x=>x.Subscriber=="EventLogger"&&x.Status=="InFlight"),"queued-delivery-observed-before-restart",20);
    await Component("BrokerA","Restart");await Control("EventLogger",new());
    await Until(async()=>!(await Broker("A",state.SessionId)).Deliveries.Any(x=>x.Status is "Pending" or "InFlight" or "DeadLetter"),"sqlite-recovers-inflight-with-idempotent-inbox");
    var before=state.Shelved.Count;await Component("GameApi","Restart");await Refresh();Check(state.Shelved.Count==before,"postgres-save-survives-api-restart");
    demo=false;await Refresh();Check(state.Shelved.Count==0,"demo-does-not-change-main-campaign");
    while(!state.Complete)await Sort();Check(state.Sparks==72,"full-72-book-campaign-through-server-rules");
    await Until(async()=> (await Get<GameSnapshot>("/api/library-game/state?demo=false")).Projections.Achievements.Any(x=>x.Key=="librarian"),"full-campaign-achievement-delivered",120);
    using(var denied=await http.GetAsync("/internal/effects"))Check(denied.StatusCode==HttpStatusCode.NotFound,"internal-effects-not-exposed-by-gateway");
    using(var denied=await new HttpClient().PostAsJsonAsync("http://localhost:5109/internal/events",new{}))Check(denied.StatusCode==HttpStatusCode.Unauthorized,"broker-requires-service-key");
    // Leave a fresh account for real browser input verification; credentials stay in ignored artifacts.
    var browserEmail=$"lr6-browser-{Guid.NewGuid():N}@example.invalid";
    await Post<JsonElement>("/api/identity/register",new{email=browserEmail,displayName="Хранитель библиотеки",password});
    var browserLogin=await Post<JsonElement>("/api/identity/login",new{email=browserEmail,password});
    Directory.CreateDirectory(Path.Combine(root,".artifacts","library-game"));
    await File.WriteAllTextAsync(Path.Combine(root,".artifacts","library-game","browser-account.json"),GameJson.Write(new{email=browserEmail,password,session=browserLogin}));
    var labEnd=await Get<JsonElement>("/api/library-game/lab");
    await File.WriteAllTextAsync(Path.Combine(output,"evidence.json"),JsonSerializer.Serialize(new{completedAt=DateTimeOffset.UtcNow,checks,lab=labEnd},new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine($"Completed {checks.Count} integration checks. Evidence: {output}");
    File.Delete(Path.Combine(output,"failure.json"));
} catch(Exception ex) {
    await File.WriteAllTextAsync(Path.Combine(output,"failure.json"),GameJson.Write(new{time=DateTimeOffset.UtcNow,checks,error=ex.ToString()}));throw;
}

