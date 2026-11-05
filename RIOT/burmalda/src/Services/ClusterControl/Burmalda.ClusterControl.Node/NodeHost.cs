using System.Diagnostics;
using System.Net.Http.Json;
using Burmalda.Raft;

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
    public NetworkFault Fault { get; private set; } = new([]);
    public string? LastControlError { get; set; }
    public string? ExecutionPauseStage { get; set; }
    private int outboundCount;
    private bool poisoned;
    public NodeHost(RaftOptions options, IRaftStorage storage, Burmalda.PostgresControl.ControlStateMachine machine,
        IConfiguration configuration, IHostApplicationLifetime lifetime, ILogger<NodeHost> logger)
    {
        Options=options; this.lifetime=lifetime; this.logger=logger;
        Core=new(options,storage,machine,initialTime:clock.ElapsedMilliseconds);
        http=new() { Timeout=TimeSpan.FromMilliseconds(options.RpcTimeoutMs) };
        http.DefaultRequestHeaders.Add("X-Raft-Key",configuration["Raft:InternalKey"]??"burmalda-raft-local-only");
        http.DefaultRequestHeaders.Add("X-Raft-Cluster",options.ClusterId);
        http.DefaultRequestHeaders.Add("X-Raft-Node",options.NodeId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
    public async Task<T> Serial<T>(Func<RaftCore,T> action,CancellationToken ct=default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if(poisoned) throw new RaftUnavailableException("Durable storage failed; this participant is stopping.");
            return action(Core);
        }
        catch(Exception e) when(e is Microsoft.Data.Sqlite.SqliteException or IOException)
        { poisoned=true;logger.LogCritical(e,"Raft durable storage failed; stopping node before any further acknowledgment"); lifetime.StopApplication(); throw; }
        finally { var messages=Core.DrainMessages(); gate.Release(); foreach(var message in messages) _=Send(message,lifetime.ApplicationStopping); }
    }
    public void SetFault(NetworkFault fault)
    {
        if(fault.Blocked.Any(x=>!Options.Members.ContainsKey(x)) || fault.DelayMs is <0 or >10000 || fault.DropEvery is <0 or >1000)
            throw new ArgumentException("Invalid fault settings");
        Fault=fault;
    }
    public async Task Inbound(int sender,CancellationToken ct)
    {
        if(Fault.Blocked.Contains(sender)) throw new RaftUnavailableException("Simulated partition");
        if(Fault.DelayMs>0) await Task.Delay(Fault.DelayMs,ct);
    }
    private async Task Send(Outbound message,CancellationToken ct)
    {
        try
        {
            var fault=Fault;
            if(fault.Blocked.Contains(message.Target) || fault.DropEvery>0 && Interlocked.Increment(ref outboundCount)%fault.DropEvery==0) return;
            if(fault.DelayMs>0) await Task.Delay(fault.DelayMs,ct);
            var path=message.Request switch { VoteRequest=>"vote",AppendRequest=>"append",SnapshotRequest=>"snapshot",_=>throw new InvalidOperationException() };
            using var response=await http.PostAsJsonAsync($"{Options.Members[message.Target]}/internal/raft/{path}",message.Request,ct);
            if(!response.IsSuccessStatusCode) return;
            switch(message.Request)
            {
                case VoteRequest v:
                    var vr=(await response.Content.ReadFromJsonAsync<VoteResponse>(cancellationToken:ct))!;
                    await Serial(n=> { n.ReceiveVote(message.Target,v,vr); return true; },ct); break;
                case AppendRequest a:
                    var ar=(await response.Content.ReadFromJsonAsync<AppendResponse>(cancellationToken:ct))!;
                    await Serial(n=> { n.ReceiveAppend(message.Target,message.Sequence,a,ar); return true; },ct); break;
                case SnapshotRequest s:
                    var sr=(await response.Content.ReadFromJsonAsync<SnapshotResponse>(cancellationToken:ct))!;
                    await Serial(n=> { n.ReceiveSnapshot(message.Target,message.Sequence,s,sr); return true; },ct); break;
            }
        }
        catch(Exception e) when(e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException) { }
        catch(Exception e) { logger.LogError(e,"Raft RPC response failed"); lifetime.StopApplication(); }
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
                if(found.Payload!=command.Payload) throw new RaftConflictException("CommandId reused");
                return new(command.CommandId,found.Result,result.Status.CommitIndex);
            }
            if(result.Status.Role!="Leader") throw new RaftUnavailableException("Leadership changed; query original CommandId");
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
                if(result is not null) return result;
                if(await Serial(n=>n.Role!=RaftRole.Leader,ct)) break;
                await Task.Delay(20,ct);
            }
            throw new RaftUnavailableException("Fresh majority did not confirm leadership");
        }
        finally { await Serial(n=> { n.EndRead(token); return true; },CancellationToken.None); }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(25));
        try { while(await timer.WaitForNextTickAsync(stoppingToken)) await Serial(n=> { n.Tick(clock.ElapsedMilliseconds); return true; },stoppingToken); }
        catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { }
    }
    public override void Dispose() { http.Dispose(); gate.Dispose(); base.Dispose(); }
}
