using System.Net;
using Burmalda.ClusterControl;
using Burmalda.Raft;
using Burmalda.PostgresControl;
using Burmalda.ConcurrencyLab.Api;

var builder=WebApplication.CreateBuilder(args);
var nodeId=builder.Configuration.GetValue("Raft:NodeId",1);
var root=PostgresReplicationCluster.ResolveRoot(builder.Configuration["ReplicationLab:ComposeRoot"]);
var runtime=Path.GetFullPath(builder.Configuration["Raft:RuntimeRoot"]??Path.Combine(root,".artifacts","raft-runtime"));
var options=new RaftOptions(nodeId,builder.Configuration["Raft:ClusterId"]??"burmalda-postgres-v1",
    RaftConfiguration.Members(builder.Configuration),
    builder.Configuration.GetValue("Raft:HeartbeatMs",200),builder.Configuration.GetValue("Raft:ElectionMinMs",1500),
    builder.Configuration.GetValue("Raft:ElectionMaxMs",3000),builder.Configuration.GetValue("Raft:RpcTimeoutMs",500),
    builder.Configuration.GetValue("Raft:SnapshotEvery",256));
builder.WebHost.UseUrls(builder.Configuration["Raft:ListenUrl"]??options.Members[nodeId]);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IRaftStorage>(_=>new SqliteRaftStorage(Path.Combine(runtime,$"node-{nodeId}","raft.db")));
builder.Services.AddSingleton<ControlStateMachine>();
builder.Services.AddSingleton<NodeHost>(); builder.Services.AddHostedService(sp=>sp.GetRequiredService<NodeHost>());
builder.Services.AddSingleton<IReplicationCluster,PostgresReplicationCluster>();
// Equipment ownership is anchored to the Compose project, not a participant's configurable data directory.
builder.Services.AddSingleton<DockerEngineClient>();
builder.Services.AddSingleton(sp=>new ExecutionJournal(
    builder.Configuration["Raft:ExecutionRoot"]??Path.Combine(root,".artifacts","raft-runtime","execution"),
    RaftConfiguration.InDocker(builder.Configuration)?sp.GetRequiredService<DockerEngineClient>():null));
builder.Services.AddHostedService<PostgresWorker>();
var app=builder.Build();
app.Use(async(context,next)=>
{
    if(!RaftConfiguration.InDocker(builder.Configuration) &&
        (context.Connection.RemoteIpAddress is not {} ip || !IPAddress.IsLoopback(ip))) { context.Response.StatusCode=403; return; }
    if(context.Request.Path.StartsWithSegments("/internal") && context.Request.Headers["X-Raft-Key"]!=(builder.Configuration["Raft:InternalKey"]??"burmalda-raft-local-only"))
    { context.Response.StatusCode=401; return; }
    try { await next(); }
    catch(RaftUnavailableException e) { context.Response.StatusCode=503; await context.Response.WriteAsJsonAsync(new { message=e.Message }); }
    catch(RaftConflictException e) { context.Response.StatusCode=422; await context.Response.WriteAsJsonAsync(new { message=e.Message }); }
    catch(ArgumentException e) { context.Response.StatusCode=400; await context.Response.WriteAsJsonAsync(new { message=e.Message }); }
});
async Task Sender(HttpContext context,int claimed,NodeHost host,CancellationToken ct)
{
    if(context.Request.Headers["X-Raft-Cluster"]!=options.ClusterId ||
        !int.TryParse(context.Request.Headers["X-Raft-Node"],out var sender) || sender!=claimed || !options.Members.ContainsKey(sender))
        throw new ArgumentException("RPC sender/cluster mismatch");
    await host.Inbound(sender,ct);
}
app.MapGet("/health/ready",()=>Results.Ok(new { nodeId }));
app.MapGet("/internal/status",(NodeHost host,CancellationToken ct)=>host.Serial(n=>n.Status(),ct));
app.MapGet("/internal/state",(NodeHost host,CancellationToken ct)=>host.Read(ct));
app.MapGet("/internal/diagnostics",async(NodeHost host,CancellationToken ct)=>new
{
    node=await host.Serial(n=>n.Status(),ct),fault=host.Fault,controlError=host.LastControlError,
    state=await host.Serial(n=>RaftJson.Read<ClusterState>(n.MachineState),ct)
});
app.MapGet("/internal/log",(long? after,int? take,NodeHost host,CancellationToken ct)=>host.Serial(n=>n.Log.Where(x=>x.Index>(after??0)).Take(Math.Clamp(take??100,1,500)).ToArray(),ct));
app.MapGet("/internal/events",(long? after,int? take,NodeHost host,CancellationToken ct)=>host.Serial(n=>n.Events.Where(x=>x.Sequence>(after??0)).Take(Math.Clamp(take??100,1,500)).ToArray(),ct));
app.MapGet("/internal/trace",(Guid? run,long? after,int? take,NodeHost host,CancellationToken ct)=>host.ReadTrace(run,after??0,take??500,ct));
app.MapPost("/internal/commands",(RaftCommand command,NodeHost host,CancellationToken ct)=>host.Submit(command,ct));
app.MapPost("/internal/raft/vote",async(VoteRequest request,HttpContext context,NodeHost host,CancellationToken ct)=>
{ await Sender(context,request.CandidateId,host,ct); host.Received(request.CandidateId,Guid.TryParse(context.Request.Headers["X-Raft-Rpc"],out var id)?id:null,request); return await host.Serial(n=>n.RequestVote(request),ct); });
app.MapPost("/internal/raft/append",async(AppendRequest request,HttpContext context,NodeHost host,CancellationToken ct)=>
{ await Sender(context,request.LeaderId,host,ct); host.Received(request.LeaderId,Guid.TryParse(context.Request.Headers["X-Raft-Rpc"],out var id)?id:null,request); return await host.Serial(n=>n.AppendEntries(request),ct); });
app.MapPost("/internal/raft/snapshot",async(SnapshotRequest request,HttpContext context,NodeHost host,CancellationToken ct)=>
{ await Sender(context,request.LeaderId,host,ct); host.Received(request.LeaderId,Guid.TryParse(context.Request.Headers["X-Raft-Rpc"],out var id)?id:null,request); return await host.Serial(n=>n.InstallSnapshot(request),ct); });
if(app.Environment.IsDevelopment()) app.MapPost("/internal/fault",(NetworkFault fault,NodeHost host)=> { host.SetFault(fault); return Results.Ok(); });
if(app.Environment.IsDevelopment()) app.MapPost("/internal/executor-fault",(ExecutorFault fault,NodeHost host)=>
{
    if(fault.MaintenanceDelayMs is <0 or >30000) return Results.BadRequest();
    host.ExecutionPauseStage=fault.PauseStage;
    builder.Configuration["Docker:MaintenanceDelayMs"]=(fault.MaintenanceDelayMs??0).ToString(System.Globalization.CultureInfo.InvariantCulture);
    return Results.Ok();
});
await app.RunAsync();

public sealed record ExecutorFault(string? PauseStage,int? MaintenanceDelayMs=null);
