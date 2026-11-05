using System.Diagnostics;
using Burmalda.Raft;
using Burmalda.PostgresControl;

namespace Burmalda.ConcurrencyLab.Api;

public sealed record NodeProcessRequest(int NodeId,string Action);
public sealed record FaultRequest(int[] Blocked,int DelayMs=0,int DropEvery=0);
public static class RaftLabEndpoints
{
    public static void MapRaftLab(this WebApplication app)
    {
        var group=app.MapGroup("/api/raft-lab");
        group.AddEndpointFilter(async(context,next)=>
        {
            try { return await next(context); }
            catch(RaftConflictException e) { return Results.Conflict(new { message=e.Message }); }
            catch(Exception e) when(e is RaftUnavailableException or HttpRequestException or TaskCanceledException)
            { return Results.Json(new { message=e.Message, outcome="Unknown", recovery="Проверьте прежний CommandId; не повторяйте с новым ID." },statusCode:503); }
        });
        group.MapGet("/nodes",(RaftControlClient control,CancellationToken ct)=>control.Nodes(ct));
        group.MapGet("/status",async(RaftControlClient control,PostgresReplicationCluster raw,CancellationToken ct)=>
        {
            var nodes=await control.Nodes(ct); AuthoritativeControl? authoritative=null; string? error=null;
            try { authoritative=await control.Read(ct); } catch(Exception e) when(e is RaftUnavailableException or HttpRequestException or TaskCanceledException) { error=e.Message; }
            var postgres=await Task.WhenAll(PostgresReplicationCluster.NodeIds.Select(async id=>
            {
                try { return await raw.Observe(id,ct); }
                catch(Exception e) when(e is System.Data.Common.DbException or ReplicationLabException or IOException)
                { return new ReplicationNode(id,"UNAVAILABLE","Unknown",true,false,null,null,false,null,null,[],[],"Нет актуального наблюдения PostgreSQL."); }
            }));
            var diagnostic=new List<object>();
            foreach(var node in nodes.Where(n=>n.Available))
            { try { diagnostic.Add(await control.Inspect<System.Text.Json.JsonElement>(node.NodeId,"/internal/diagnostics",ct)); } catch(HttpRequestException) { } }
            return Results.Ok(new { nodes,majority=3,authoritative,state=authoritative?.State,postgres,diagnostic,error,observedAtUtc=DateTimeOffset.UtcNow });
        });
        group.MapGet("/log",async(int? nodeId,long? after,int? take,RaftControlClient control,CancellationToken ct)=>
        {
            var id=nodeId??(await control.Nodes(ct)).FirstOrDefault(n=>n.State?.Role=="Leader")?.NodeId??1;
            var nodes=await control.Nodes(ct); var status=nodes.Single(n=>n.NodeId==id).State;
            var entries=await control.Inspect<LogEntry[]>(id,$"/internal/log?after={after??0}&take={Math.Clamp(take??100,1,500)}",ct);
            return Results.Ok(new { nodeId=id,observedAtUtc=DateTimeOffset.UtcNow,snapshotIndex=status?.SnapshotIndex,
                entries=entries.Select(e=>new { e.Index,e.Term,commandId=e.Command?.CommandId,payload=e.Command?.Payload,persisted=true,committed=e.Index<=status?.CommitIndex,applied=e.Index<=status?.LastApplied }) });
        });
        group.MapGet("/events",async(int? nodeId,long? after,int? take,RaftControlClient control,CancellationToken ct)=>
        {
            var id=nodeId??(await control.Nodes(ct)).FirstOrDefault(n=>n.State?.Role=="Leader")?.NodeId??1;
            return Results.Ok(new { nodeId=id,observedAtUtc=DateTimeOffset.UtcNow,
                events=await control.Inspect<RaftEvent[]>(id,$"/internal/events?after={after??0}&take={Math.Clamp(take??100,1,500)}",ct) });
        });
        group.MapGet("/operations/{id:guid}",async(Guid id,RaftControlClient control,CancellationToken ct)=>
        {
            var view=await control.Read(ct);
            return view.State.Operations.TryGetValue(id,out var op)?Results.Ok(op):Results.NotFound(new { commandId=id,message="Операция пока не применена; это не доказывает, что отправленная команда не будет подтверждена позднее." });
        });
        group.MapPost("/operations",async(OperationRequest request,RaftControlClient control,CancellationToken ct)=>
        {
            if(request.CommandId==Guid.Empty || !new[]{"Switchover","Fence","Stop","Promote","Recover","Pause","Resume","Synchronize"}.Contains(request.Kind))
                return Results.BadRequest(new { message="Нужны CommandId и допустимый Kind." });
            if(request.Node is not null) PostgresReplicationCluster.CheckNode(request.Node);
            if(request.Kind is "Fence" or "Stop" or "Recover" && request.Node is null) return Results.BadRequest(new { message="Для операции нужен Node." });
            try { var op=await control.Submit(request,ct); return Results.Accepted($"/api/raft-lab/operations/{request.CommandId}",op); }
            catch(Exception e) when(e is RaftUnavailableException or TaskCanceledException or HttpRequestException)
            { return Results.Json(new { request.CommandId,message=e.Message,outcome="Unknown",statusUrl=$"/api/raft-lab/operations/{request.CommandId}" },statusCode:503); }
        });
        group.MapPost("/faults/{nodeId:int}",async(int nodeId,FaultRequest fault,RaftControlClient control,CancellationToken ct)=>
        {
            if(!control.Members.ContainsKey(nodeId)) return Results.BadRequest();
            await control.Fault(nodeId,fault,ct); return Results.Ok();
        });
        group.MapPost("/processes",async(NodeProcessRequest request,IConfiguration config,CancellationToken ct)=>
        {
            if(request.NodeId is <1 or >5 || request.Action is not("Start" or "Stop" or "Restart")) return Results.BadRequest();
            var root=PostgresReplicationCluster.ResolveRoot(config["ReplicationLab:ComposeRoot"]);
            var info=new ProcessStartInfo("pwsh") { WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true };
            foreach(var arg in new[]{"-NoProfile","-File",Path.Combine(root,"tools","Set-RaftNode.ps1"),"-NodeId",request.NodeId.ToString(),"-Action",request.Action}) info.ArgumentList.Add(arg);
            using var process=Process.Start(info)!;
            await process.WaitForExitAsync(ct);
            return process.ExitCode==0?Results.Ok(new { message=$"Node {request.NodeId}: {request.Action}" }):Results.Conflict(new { message="Управление процессом не выполнено. Проверьте raft-runtime и журнал API." });
        });
    }
}
