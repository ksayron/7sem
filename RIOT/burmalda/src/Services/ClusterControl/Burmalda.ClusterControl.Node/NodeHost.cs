using System.Diagnostics;
using System.Net.Http.Json;
using Burmalda.Raft;
using Burmalda.Diagnostics;

namespace Burmalda.ClusterControl;

public sealed record NetworkFault(int[] Blocked, int DelayMs = 0, int DropEvery = 0);
public sealed class NodeHost : BackgroundService
{
    private readonly SemaphoreSlim gate = new(1,1);
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly HttpClient http;
    private readonly IHostApplicationLifetime lifetime;
    private readonly ILogger<NodeHost> logger;
    public RaftCore Core { get; }
    public RaftOptions Options { get; }
    public TraceBuffer Trace { get; }
    private long tracedEvent;
    private TraceState? lastTracedState;
    public NetworkFault Fault { get; private set; } = new([]);
    public string? LastControlError { get; set; }
    public string? ExecutionPauseStage { get; set; }
    private int outboundCount;
    private bool poisoned;
    public NodeHost(RaftOptions options, IRaftStorage storage, Burmalda.PostgresControl.ControlStateMachine machine,
        IConfiguration configuration, IHostApplicationLifetime lifetime, ILogger<NodeHost> logger, HttpMessageHandler? transport=null)
    {
        Options=options;
        this.lifetime=lifetime;
        this.logger=logger;
        Trace=new(options.NodeId);
        Core=new(options,storage,machine,initialTime:clock.ElapsedMilliseconds);
        http=transport is null?new():new(transport);
        http.Timeout=TimeSpan.FromMilliseconds(options.RpcTimeoutMs);
        http.DefaultRequestHeaders.Add("X-Raft-Key",configuration["Raft:InternalKey"]??"burmalda-raft-local-only");
        http.DefaultRequestHeaders.Add("X-Raft-Cluster",options.ClusterId);
        http.DefaultRequestHeaders.Add("X-Raft-Node",options.NodeId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
    public async Task<T> Serial<T>(Func<RaftCore,T> action,CancellationToken ct=default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if(poisoned) 
                throw new RaftUnavailableException("Durable storage failed; this participant is stopping.");
            var result=action(Core);
            Capture();
            return result;
        }
        catch(Exception e) when(e is Microsoft.Data.Sqlite.SqliteException or IOException)
        { 
            poisoned=true;
            logger.LogCritical(e,"Raft durable storage failed; stopping node before any further acknowledgment"); 
            lifetime.StopApplication(); 
            throw; 
        }
        finally { 
            var messages=Core.DrainMessages(); 
            gate.Release(); 
            foreach(var message in messages) 
                _=Send(message,lifetime.ApplicationStopping); 
        }
    }
    private void Capture()
    {
        try
        {
            var status=Core.Status();
            var changed=lastTracedState is null || lastTracedState.Role!=status.Role || lastTracedState.Term!=status.Term ||
                lastTracedState.CommitIndex!=status.CommitIndex || lastTracedState.Applied!=status.LastApplied || lastTracedState.LastIndex!=status.LastLogIndex;
            if(!changed && tracedEvent==Core.EventSequence) 
                return;
            var checkpoint=Core.TraceState();
            foreach(var e in Core.Events.Where(x=>x.Sequence>tracedEvent))
                Trace.Add(e.Kind,e.Detail,term:e.Term,index:checkpoint.LastIndex,state:checkpoint);
            tracedEvent=Core.EventSequence;
            if(changed) 
                Trace.Add("State","Роль и бортовой журнал обновлены",term:status.Term,index:status.LastLogIndex,state:checkpoint);
            lastTracedState=checkpoint;
        }
        catch(Exception) { /* Best effort only. */ }
    }
    public async Task<object> ReadTrace(Guid? run,long after,int take,CancellationToken ct)
    {
        Burmalda.PostgresControl.AuthoritativeControl? authority=null;
        if(await Serial(n=>n.Role==RaftRole.Leader,ct))
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(180);
            try { authority=await Read(deadline.Token); }
            catch(Exception e) when(e is RaftUnavailableException or OperationCanceledException) { }
        }
        var checkpoint=await Serial(n=>new TraceCheckpoint(n.TraceState(),Fault.Blocked,
            authority is not null && n.Role==RaftRole.Leader && n.Term==authority.Node.Term),ct);
        return new { trace=Trace.Read(run,after,take),checkpoint };
    }
    private static (string Rpc,long Term,long? Index) Metadata(object request)=>request switch
    {
        VoteRequest v=>("RequestVote",v.Term,v.LastLogIndex),
        AppendRequest a=>(a.Entries.Length==0?"Heartbeat":"AppendEntries",a.Term,a.PrevLogIndex+a.Entries.Length),
        SnapshotRequest s=>("InstallSnapshot",s.Term,s.Snapshot.Index),
        _=>("Unknown",0,null)
    };
    public void Received(int sender,Guid? rpcId,object request)
    {
        var m=Metadata(request);
        Trace.Add("Received","Запрос получен; проверка и сохранение ещё впереди",sender,Options.NodeId,rpcId,m.Rpc,m.Term,m.Index);
    }
    public void SetFault(NetworkFault fault)
    {
        if(fault.Blocked.Any(x=>!Options.Members.ContainsKey(x)) || fault.DelayMs is <0 or >10000 || fault.DropEvery is <0 or >1000)
            throw new ArgumentException("Invalid fault settings");
        Fault=fault;
        Trace.Add("Fault","Настройки учебного канала изменены",term:Core.Term);
    }
    public async Task Inbound(int sender,CancellationToken ct)
    {
        if(Fault.Blocked.Contains(sender)) 
            throw new RaftUnavailableException("Simulated partition");
        if(Fault.DelayMs>0) 
            await Task.Delay(Fault.DelayMs,ct);
    }
    private async Task Send(Outbound message,CancellationToken ct)
    {
        var rpcId=Guid.NewGuid(); 
        var m=Metadata(message.Request);
        void Record(string kind,string detail,bool? success=null,bool response=false)=>Trace.Add(kind,detail,
            response?message.Target:Options.NodeId,response?Options.NodeId:message.Target,rpcId,m.Rpc,m.Term,m.Index,success);
        try
        {
            var fault=Fault;
            if(fault.Blocked.Contains(message.Target)) { Record("Blocked","Связь заблокирована учебным разделением",false); return; }
            if(fault.DropEvery>0 && Interlocked.Increment(ref outboundCount)%fault.DropEvery==0) { Record("Dropped","Сообщение намеренно потеряно",false); return; }
            Record("Sent","Запрос отправлен");
            if(fault.DelayMs>0) 
                await Task.Delay(fault.DelayMs,ct);
            var path=message.Request switch { VoteRequest=>"vote",AppendRequest=>"append",SnapshotRequest=>"snapshot",_=>throw new InvalidOperationException() };
            using var request=new HttpRequestMessage(HttpMethod.Post,$"{Options.Members[message.Target]}/internal/raft/{path}") { Content=JsonContent.Create(message.Request) };
            request.Headers.Add("X-Raft-Rpc",rpcId.ToString());
            using var response=await http.SendAsync(request,ct);
            if(!response.IsSuccessStatusCode) { Record("Rejected",$"RPC отклонён: HTTP {(int)response.StatusCode}",false,true); return; }
            switch(message.Request)
            {
                case VoteRequest v:
                    var vr=(await response.Content.ReadFromJsonAsync<VoteResponse>(cancellationToken:ct))!;
                    Record("Response",vr.Granted?"Голос отдан":"Голос отклонён",vr.Granted,true);
                    await Serial(n=> { 
                        n.ReceiveVote(message.Target,v,vr); 
                        return true; },ct); break;
                case AppendRequest a:
                    var ar=(await response.Content.ReadFromJsonAsync<AppendResponse>(cancellationToken:ct))!;
                    Record("Response",ar.Success?(m.Rpc=="Heartbeat"?"Есть связь":"Запись сохранена"):"Журнал не совпал или term устарел",ar.Success,true);
                    await Serial(n=> { 
                        n.ReceiveAppend(message.Target,message.Sequence,a,ar); 
                        return true; },ct); break;
                case SnapshotRequest s:
                    var sr=(await response.Content.ReadFromJsonAsync<SnapshotResponse>(cancellationToken:ct))!;
                    Record("Response","Снимок обработан",sr.MatchIndex>=s.Snapshot.Index,true);
                    await Serial(n=> { 
                        n.ReceiveSnapshot(message.Target,message.Sequence,s,sr); 
                        return true; },ct); break;
            }
        }
        catch(Exception e) when(e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        { 
            Record(e is OperationCanceledException?"Timeout":"TransportError",e is OperationCanceledException?"Ответ не получен вовремя; это не доказывает остановку узла":"Ответ недоступен или повреждён",false); }
        catch(Exception e) { 
            logger.LogError(e,"Raft RPC response failed"); 
            lifetime.StopApplication(); 
        }
    }
    public async Task<Burmalda.PostgresControl.CommandReceipt> Submit(RaftCommand command,CancellationToken ct)
    {
        await Serial(n=>n.Propose(command),ct);
        var deadline=clock.ElapsedMilliseconds+5000;
        while(clock.ElapsedMilliseconds<deadline)
        {
            var result=await Serial(n=>(Result:n.Result(command.CommandId),Status:n.Status()),ct);
            if(result.Result is {} found)
            {
                if(found.Payload!=command.Payload) 
                    throw new RaftConflictException("CommandId reused");
                return new(command.CommandId,found.Result,result.Status.CommitIndex);
            }
            if(result.Status.Role!="Leader") 
                throw new RaftUnavailableException("Leadership changed; query original CommandId");
            await Task.Delay(20,ct);
        }
        throw new RaftUnavailableException("Quorum did not confirm command; outcome unknown, keep CommandId");
    }
    public async Task<Burmalda.PostgresControl.AuthoritativeControl> Read(CancellationToken ct)
    {
        var token=await Serial(n=>n.BeginRead(),ct);
        try
        {
            var deadline=clock.ElapsedMilliseconds+3000;
            while(clock.ElapsedMilliseconds<deadline)
            {
                var result=await Serial(n=>n.ReadConfirmed(token)
                    ? new Burmalda.PostgresControl.AuthoritativeControl(n.Status() with { Authoritative=true },RaftJson.Read<Burmalda.PostgresControl.ClusterState>(n.MachineState)) : null,ct);
                if(result is not null) 
                    return result;
                if(await Serial(n=>n.Role!=RaftRole.Leader,ct)) 
                    break;
                await Task.Delay(20,ct);
            }
            throw new RaftUnavailableException("Fresh majority did not confirm leadership");
        }
        finally { 
            await Serial(n=> { 
                n.EndRead(token); 
                return true; },CancellationToken.None); 
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(25));
        try 
        {
            while(await timer.WaitForNextTickAsync(stoppingToken)) 
                await Serial(n=> { 
                    n.Tick(clock.ElapsedMilliseconds); 
                    return true; },stoppingToken); 
        }
        catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { }
    }
    public override void Dispose() { 
        http.Dispose(); 
        gate.Dispose(); 
        base.Dispose(); 
    }
}
