using System.Security.Cryptography;
using System.Text;
using Burmalda.Raft;
using Burmalda.PostgresControl;
using Burmalda.ConcurrencyLab.Api;

namespace Burmalda.ClusterControl;

public sealed class PostgresWorker(NodeHost host,IReplicationCluster postgres,ExecutionJournal journal,
    IConfiguration config,ILogger<PostgresWorker> logger) : BackgroundService
{
    private DateTimeOffset nextMaintenanceCleanup;
    public static Guid Id(string text) => new(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0,16));
    private async Task Record(ControlEntry entry,CancellationToken ct)
    {
        var payload=RaftJson.Write(entry);
        var receipt=await host.Submit(new(Id($"internal:{entry.OperationId}:{payload}"),payload),ct);
        var ack=RaftJson.Read<ControlAck>(receipt.Result);
        if(!ack.Accepted) throw new RaftConflictException(ack.Error!);
    }
    private Task Stage(ClusterOperation op,string stage,CancellationToken ct,string? value=null,string? source=null,string? target=null)
        =>Record(new("Stage",op.Request.CommandId,Stage:stage,Value:value,Source:source,Target:target,ExpectedStage:op.Stage),ct);
    private async Task Complete(ClusterOperation op,CancellationToken ct,string? value=null)
    {
        await Record(new("Complete",op.Request.CommandId,Value:value,ExpectedStage:op.Stage),ct);
        if(op.Request.Kind=="Recover") await CleanupMaintenance([op.Request.CommandId],ct);
    }
    private async Task CleanupMaintenance(IEnumerable<Guid> committedCompleted,CancellationToken ct)
    {
        if(postgres is not IMaintenanceCleanup cleanup) return;
        try
        {
            var removed=await cleanup.CleanupCompletedMaintenance(journal.CompletedRecoveries(committedCompleted),ct);
            if(removed>0) logger.LogInformation("Removed {Count} completed maintenance containers",removed);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
        catch(Exception e) { logger.LogWarning("Maintenance cleanup deferred: {Message}",e.Message); }
    }
    private async Task<ReplicationNode[]> Observe(CancellationToken ct)
    {
        return await Task.WhenAll(PostgresReplicationCluster.NodeIds.Select(async id=>
        {
            try { return await postgres.Observe(id,ct); }
            catch(Exception e) when(e is System.Data.Common.DbException or ReplicationLabException or IOException)
            { return new ReplicationNode(id,"UNAVAILABLE","Unknown",true,false,null,null,false,null,null,[],[],"PostgreSQL недоступен"); }
        }));
    }
    private static bool Writer(ReplicationNode n)=>n.State=="AVAILABLE" && n.Role=="Primary" && !n.ReadOnly && !n.Fenced;
    private static ReplicationNode? Writer(ReplicationNode[] nodes)
    {
        if(nodes.Count(Writer)>1) throw new ReplicationLabException(409,"Два writable primary: любые управляющие операции заблокированы.");
        return nodes.SingleOrDefault(Writer);
    }
    public static bool Replayed(string? replay,string? required)
    {
        static ulong Parse(string value) { var p=value.Split('/'); return (Convert.ToUInt64(p[0],16)<<32)+Convert.ToUInt64(p[1],16); }
        return replay is not null && required is not null && Parse(replay)>=Parse(required);
    }
    private void CheckLegacy()
    {
        if(config["ReplicationLab:LegacyRoot"] is {} legacy) LegacyControlGuard.Check(legacy);
        var root=PostgresReplicationCluster.ResolveRoot(config["ReplicationLab:ComposeRoot"]);
        var path=Path.Combine(root,".artifacts","streaming-replication","authority.json");
        if(!File.Exists(path)) return;
        using var document=System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        if(document.RootElement.TryGetProperty("fencedSource",out var source) && source.ValueKind!=System.Text.Json.JsonValueKind.Null)
            throw new ReplicationLabException(409,"Незавершённое состояние координатора ЛР5: перед активацией Raft завершите восстановление старого узла.");
        var directory=Path.GetDirectoryName(path)!;
        foreach(var file in Directory.EnumerateFiles(directory,"session-*.json"))
        {
            using var session=System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            if(session.RootElement.TryGetProperty("actionStatus",out var status) && status.GetString()=="Running")
                throw new ReplicationLabException(409,"Старая операция ЛР5 не завершена. Активация Raft заблокирована.");
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if(await host.Serial(n=>n.Role==RaftRole.Leader,stoppingToken)) await Work(stoppingToken);
                else host.LastControlError=null;
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { break; }
            catch(Exception e)
            {
                if(host.LastControlError!=e.Message) logger.LogWarning("PostgreSQL control deferred: {Message}",e.Message);
                host.LastControlError=e.Message;
            }
            try { await Task.Delay(400,stoppingToken); } catch(OperationCanceledException) { break; }
        }
    }
    private async Task Work(CancellationToken ct)
    {
        using var gate=journal.TryLock(); if(gate is null) return;
        journal.BindCluster(host.Options.ClusterId+"|"+string.Join('|',host.Options.Members.OrderBy(x=>x.Key).Select(x=>$"{x.Key}={x.Value}")));
        var view=await host.Read(ct); var state=view.State;
        if(!state.Initialized)
        {
            CheckLegacy(); var nodes=await Observe(ct); var primary=Writer(nodes)??throw new ReplicationLabException(503,"Для первичной активации нужен ровно один подтверждённый writable PostgreSQL.");
            if(nodes.Any(n=>n.Fenced)) throw new ReplicationLabException(409,"Fenced PostgreSQL при активации: сначала завершите прежнее восстановление.");
            await Record(new("Bootstrap",Guid.Empty,Value:primary.Id),ct); host.LastControlError=null; return;
        }
        if(DateTimeOffset.UtcNow>=nextMaintenanceCleanup)
        {
            nextMaintenanceCleanup=DateTimeOffset.UtcNow+TimeSpan.FromSeconds(30);
            await CleanupMaintenance(state.Operations.Where(x=>x.Value.Status=="Completed" && x.Value.Request.Kind=="Recover" && state.ActiveOperation!=x.Key).Select(x=>x.Key),ct);
        }
        if(state.ActiveOperation is not {} id)
        {
            if(state.FencedSource is not null && state.RequiredLsn is not null)
            {
                var nodes=await Observe(ct);
                if(Writer(nodes) is null && await postgres.IsStopped(state.FencedSource,ct))
                {
                    var target=nodes.SingleOrDefault(n=>n.Id!=state.FencedSource && n.Role=="Standby" && !n.ReplayPaused && Replayed(n.ReplayLsn,state.RequiredLsn));
                    if(target is not null)
                    {
                        var request=new OperationRequest(Id($"auto:{state.FenceOperation}:{target.Id}"),"Promote",target.Id);
                        if(!state.Operations.ContainsKey(request.CommandId)) await Record(new("Begin",request.CommandId,request),ct);
                    }
                }
            }
            return;
        }
        var op=state.Operations[id]; if(op.Status is not("Running" or "NeedsInspection")) return;
        if(host.ExecutionPauseStage==op.Stage) return;
        try { await Step(state,op,ct); host.LastControlError=null; }
        catch(RaftUnavailableException) { throw; }
        catch(ExternalActionRunningException) { throw; }
        catch(OperationCanceledException) { throw; }
        catch(NeedsInspectionException e) { await Record(new("NeedsInspection",id,Value:e.Message),ct); host.LastControlError=e.Message; }
        catch(Exception e) when(e is ReplicationLabException or RaftConflictException or System.Data.Common.DbException or IOException)
        {
            // An unresolved external effect must never be cleared by a later validation failure.
            if(op.Status!="NeedsInspection") await Record(new(journal.HasUnresolved(id)?"NeedsInspection":"Failed",id,Value:e.Message),ct);
            host.LastControlError=e.Message;
        }
    }
    private async Task Step(ClusterState state,ClusterOperation op,CancellationToken ct)
    {
        var kind=op.Request.Kind; var id=op.Request.CommandId;
        if(op.Stage=="Begin")
        {
            var nodes=await Observe(ct); var writer=Writer(nodes);
            string? source=writer?.Id,target=op.Request.Node;
            switch(kind)
            {
                case "Switchover":
                    if(writer is null || state.FencedSource is not null) throw new ReplicationLabException(409,"Переключение требует writable primary и восстановленной пары PostgreSQL.");
                    target??=nodes.SingleOrDefault(n=>n.Role=="Standby")?.Id;
                    if(target==source || nodes.SingleOrDefault(n=>n.Id==target)?.Role!="Standby") throw new ReplicationLabException(409,"Цель должна быть доступным standby.");
                    break;
                case "Fence":
                    source=op.Request.Node;
                    if(state.FencedSource==source && state.RequiredLsn is not null) { await Complete(op,ct,state.RequiredLsn); return; }
                    if(writer?.Id!=source || state.FencedSource is not null) throw new ReplicationLabException(409,"Fencing требует текущий writable primary без прежнего fencing.");
                    target=nodes.Single(n=>n.Id!=source).Id; break;
                case "Promote":
                    source=state.FencedSource; target??=nodes.SingleOrDefault(n=>n.Role=="Standby")?.Id;
                    if(source is null || state.RequiredLsn is null || target is null || source==target) throw new ReplicationLabException(503,"Без подтверждённого fencing/WAL promotion запрещён.");
                    break;
                case "Stop":
                    if(target is null) throw new ReplicationLabException(400,"Нужен PostgreSQL node.");
                    if((writer?.Id==target || state.Primary==target) && (state.FencedSource!=target || state.RequiredLsn is null))
                        throw new ReplicationLabException(409,"Сначала согласуйте Fence или Switchover текущего primary.");
                    break;
                case "Recover":
                    if(target is null || writer?.Id==target) throw new ReplicationLabException(409,"Нельзя восстановить работающий primary.");
                    if(writer is null && state.Primary!=target) throw new ReplicationLabException(503,"Источник восстановления недоступен.");
                    source=writer?.Id??target; break;
                case "Pause": case "Resume": case "Synchronize":
                    target??=nodes.SingleOrDefault(n=>n.Role=="Standby")?.Id;
                    if(writer is null || target is null || target==source || nodes.Single(n=>n.Id==target).Role!="Standby") throw new ReplicationLabException(409,"Нужна доступная пара primary/standby.");
                    break;
                default: throw new ReplicationLabException(400,"Неизвестная управляющая операция.");
            }
            await Stage(op,"Planned",ct,source:source,target:target); return;
        }
        if(kind is "Switchover" or "Fence")
        {
            if(op.Stage=="Planned")
            {
                var lsn=await journal.Run(id,"Fence",()=>postgres.QuiesceAndFence(op.Source!,ct),()=>Task.FromResult<string?>(null));
                await Stage(op,"SourceFenced",ct,lsn); return;
            }
            if(kind=="Fence") { await Complete(op,ct,state.RequiredLsn); return; }
            if(op.Stage=="SourceFenced")
            {
                await journal.Run(id,"Stop",async()=> { await postgres.Stop(op.Source!,ct); if(!await postgres.IsStopped(op.Source!,ct)) throw new IOException("Source still running"); return "Stopped"; },
                    async()=>await postgres.IsStopped(op.Source!,ct)?"Stopped":null);
                await Stage(op,"SourceStopped",ct); return;
            }
        }
        if(kind=="Promote" || kind=="Switchover" && op.Stage is "SourceStopped" or "PromotionAuthorized" or "PrimaryActivated")
        {
            if(op.Stage is "Planned" or "SourceStopped")
            {
                await CheckPromotion(state,op,false,ct); await Stage(op,"PromotionAuthorized",ct); return;
            }
            if(op.Stage=="PromotionAuthorized")
            {
                await CheckPromotion(state,op,true,ct);
                await journal.Run(id,"Promote",async()=> { await postgres.Promote(op.Target!,ct); var n=await postgres.Observe(op.Target!,ct); if(!Writer(n)) throw new IOException("Target did not become writable primary"); return n.Id; },
                    async()=>Writer(await postgres.Observe(op.Target!,ct))?op.Target:null);
                if(host.ExecutionPauseStage=="AfterPromotionEffect") return;
                await Stage(op,"PrimaryActivated",ct); return;
            }
            await Complete(op,ct,op.Target); return;
        }
        if(kind=="Stop")
        {
            await journal.Run(id,"Stop",async()=> { await postgres.Stop(op.Target!,ct); if(!await postgres.IsStopped(op.Target!,ct)) throw new IOException("Node still running"); return "Stopped"; },
                async()=>await postgres.IsStopped(op.Target!,ct)?"Stopped":null);
            await Complete(op,ct,"Stopped"); return;
        }
        if(kind=="Recover")
        {
            if(op.Stage=="Planned")
            {
                await journal.Run(id,"Recover",async()=>
                {
                    var existing=(await Observe(ct)).Single(n=>n.Id==op.Target);
                    var alreadyRecovered=op.Source==op.Target?Writer(existing):existing.State=="AVAILABLE" && existing.Role=="Standby" && !existing.Fenced;
                    if(!alreadyRecovered)
                    {
                        if(state.FencedSource==op.Target)
                        {
                            // A refused Docker restart may leave the startup-fenced container restarting.
                            await postgres.Stop(op.Target!,ct);
                            if(op.Source==op.Target) await postgres.RestoreSource(op.Target!,ct); else await postgres.Recover(op.Target!,op.Source!,ct);
                        }
                        else await postgres.Start(op.Target!,ct);
                    }
                    await WaitFor(async()=>
                    {
                        var actual=(await Observe(ct)).Single(n=>n.Id==op.Target);
                        return op.Source==op.Target?Writer(actual):actual.State=="AVAILABLE" && actual.Role=="Standby" && !actual.Fenced;
                    },ct);
                    return "Recovered";
                },async()=>
                {
                    var nodes=await Observe(ct); var target=nodes.Single(n=>n.Id==op.Target);
                    if(target.State=="AVAILABLE" && !target.Fenced && (op.Source==op.Target?Writer(target):target.Role=="Standby"))return "Recovered";
                    if(state.FencedSource==op.Target &&
                        (op.Source==op.Target?Writer(nodes) is null:Writer(nodes)?.Id==op.Source))
                    {
                        // Same committed recovery, same durable job name. Maintenance reattaches to
                        // an existing job and never reruns a failed/completed pg_rewind container.
                        await postgres.Stop(op.Target!,ct);
                        if(op.Source==op.Target)await postgres.RestoreSource(op.Target!,ct);
                        else await postgres.Recover(op.Target!,op.Source!,ct);
                        await WaitFor(async()=>
                        {
                            var actual=await postgres.Observe(op.Target!,ct);
                            return op.Source==op.Target?Writer(actual):actual.Role=="Standby" && !actual.Fenced;
                        },ct);
                        return "Recovered";
                    }
                    // A stopped, still-assigned primary can resume the very same committed Start.
                    // No rewind, fence removal or new primary assignment is inferred here.
                    if(op.Source==op.Target && state.Primary==op.Target && state.FencedSource!=op.Target && Writer(nodes) is null && await postgres.IsStopped(op.Target!,ct))
                    {
                        await postgres.Start(op.Target!,ct);
                        await WaitFor(async()=>Writer(await postgres.Observe(op.Target!,ct)),ct);
                        return "Recovered";
                    }
                    return null;
                });
                await Stage(op,"Recovered",ct); return;
            }
            await Complete(op,ct,"Recovered"); return;
        }
        if(kind is "Pause" or "Resume" or "Synchronize")
        {
            var paused=kind=="Pause";
            await journal.Run(id,"Replay",async()=> { await postgres.Pause(op.Target!,paused,ct); return paused?"Paused":"Resumed"; },
                async()=> (await postgres.Observe(op.Target!,ct)).ReplayPaused==paused?(paused?"Paused":"Resumed"):null);
            if(kind=="Synchronize")
            {
                var boundary=(await postgres.Observe(op.Source!,ct)).WalLsn;
                await WaitFor(async()=>Replayed((await postgres.Observe(op.Target!,ct)).ReplayLsn,boundary),ct);
            }
            await Complete(op,ct,paused?"Paused":"Replayed"); return;
        }
    }
    private async Task CheckPromotion(ClusterState state,ClusterOperation op,bool allowAlreadyPrimary,CancellationToken ct)
    {
        if(state.FencedSource!=op.Source || state.RequiredLsn is null || !await postgres.IsStopped(op.Source!,ct))
            throw new ReplicationLabException(409,"Fencing/остановка исходного primary не подтверждены.");
        var nodes=await Observe(ct); var writer=Writer(nodes); var target=nodes.Single(n=>n.Id==op.Target);
        if(allowAlreadyPrimary && writer?.Id==op.Target) return;
        if(writer is not null || target.Role!="Standby" || target.ReplayPaused || !Replayed(target.ReplayLsn,state.RequiredLsn))
            throw new ReplicationLabException(409,"Standby недоступен, replay приостановлен или нужный WAL не воспроизведён. Promotion запрещён.");
    }
    private static async Task WaitFor(Func<Task<bool>> condition,CancellationToken ct)
    {
        var deadline=DateTimeOffset.UtcNow+TimeSpan.FromSeconds(25);
        while(DateTimeOffset.UtcNow<deadline)
        {
            try { if(await condition()) return; }
            catch(Exception error) when(error is System.Data.Common.DbException or System.Net.Sockets.SocketException or TimeoutException or ReplicationLabException { Status:503 })
            { /* A just-started PostgreSQL can refuse connections until recovery finishes. Retry observations only. */ }
            await Task.Delay(300,ct);
        }
        throw new ReplicationLabException(503,"Истекло время ожидания PostgreSQL/WAL; автоматического обхода проверки нет.");
    }
}
