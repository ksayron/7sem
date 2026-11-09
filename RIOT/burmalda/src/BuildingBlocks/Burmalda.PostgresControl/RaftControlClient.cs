using System.Net.Http.Json;
using Burmalda.Raft;
using Burmalda.Diagnostics;
using Microsoft.Extensions.Configuration;

namespace Burmalda.PostgresControl;

public sealed record NodeObservation(int NodeId, bool Available, RaftStatus? State, DateTimeOffset ObservedAtUtc, string? Error);
public sealed record AuthoritativeControl(RaftStatus Node, ClusterState State);
public sealed record CommandReceipt(Guid CommandId, string Result, long CommitIndex);
public sealed class RaftControlClient
{
    public readonly Dictionary<int, string> Members;
    private readonly HttpClient http;
    public RaftControlClient(IConfiguration configuration)
    {
        Members = RaftConfiguration.Members(configuration);
        http = new() { Timeout = TimeSpan.FromSeconds(7) };
        http.DefaultRequestHeaders.Add("X-Raft-Key", configuration["Raft:InternalKey"] ?? "burmalda-raft-local-only");
    }
    public async Task<NodeObservation[]> Nodes(CancellationToken ct)
        => await Task.WhenAll(Members.Select(async pair =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMilliseconds(800));
            try { return new NodeObservation(pair.Key,true,await http.GetFromJsonAsync<RaftStatus>($"{pair.Value}/internal/status",timeout.Token),DateTimeOffset.UtcNow,null); }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
            { return new(pair.Key,false,null,DateTimeOffset.UtcNow,"Узел недоступен; состояние не подтверждено."); }
        }));
    public async Task<T> Leader<T>(string path, object? body, CancellationToken ct)
    {
        var nodes = await Nodes(ct);
        foreach (var n in nodes.Where(x=>x.Available).OrderByDescending(x=>x.State?.Term??0).ThenByDescending(x=>x.State?.Role=="Leader"))
        {
            try
            {
                using var response = body is null
                    ? await http.GetAsync(Members[n.NodeId]+path,ct)
                    : await http.PostAsJsonAsync(Members[n.NodeId]+path,body,ct);
                if ((int)response.StatusCode is 409 or 503) continue;
                if (!response.IsSuccessStatusCode) throw new RaftConflictException(await response.Content.ReadAsStringAsync(ct));
                return (await response.Content.ReadFromJsonAsync<T>(cancellationToken:ct))!;
            }
            catch(HttpRequestException) { }
            catch(OperationCanceledException) when(!ct.IsCancellationRequested) { }
        }
        throw new RaftUnavailableException("Нет подтверждённого лидера/кворума Raft. Исход команды может быть неизвестен; проверяйте прежний CommandId.");
    }
    public Task<AuthoritativeControl> Read(CancellationToken ct) => Leader<AuthoritativeControl>("/internal/state",null,ct);
    public async Task<ClusterOperation> Submit(OperationRequest request, CancellationToken ct)
    {
        var ack = await Leader<CommandReceipt>("/internal/commands",new RaftCommand(request.CommandId,RaftJson.Write(new ControlEntry("Begin",request.CommandId,request))),ct);
        var result=RaftJson.Read<ControlAck>(ack.Result);
        if(!result.Accepted) throw new RaftConflictException(result.Error!);
        return (await Read(ct)).State.Operations[request.CommandId];
    }
    public async Task<ClusterOperation> Execute(string kind,string? node,CancellationToken ct)
    {
        var request=new OperationRequest(Guid.NewGuid(),kind,node);
        await Submit(request,ct);
        var deadline=DateTimeOffset.UtcNow+TimeSpan.FromMinutes(4);
        while(DateTimeOffset.UtcNow<deadline)
        {
            var op=(await Read(ct)).State.Operations[request.CommandId];
            if(op.Status=="Completed") return op;
            if(op.Status is "Failed" or "NeedsInspection") throw new RaftConflictException(op.Error??op.Status);
            await Task.Delay(250,ct);
        }
        throw new RaftUnavailableException($"Операция {request.CommandId} не завершена; не повторяйте с новым ID.");
    }
    public async Task<T> Inspect<T>(int id,string path,CancellationToken ct)
        => (await http.GetFromJsonAsync<T>(Members[id]+path,ct))!;
    public async Task<VisualizationFrame> Visualization(string? cursors,CancellationToken ct)
    {
        var positions=new Dictionary<int,(Guid Run,long After)>();
        if(cursors is { Length:>1024 }) throw new ArgumentException("Cursor too long");
        foreach(var part in (cursors??"").Split(';',StringSplitOptions.RemoveEmptyEntries))
        {
            var bits=part.Split(',');
            if(bits.Length!=3 || !int.TryParse(bits[0],out var id) || !Members.ContainsKey(id) ||
                !Guid.TryParse(bits[1],out var run) || !long.TryParse(bits[2],out var after) || after<0 || !positions.TryAdd(id,(run,after)))
                throw new ArgumentException("Invalid visualization cursor");
        }
        var nodes=await Task.WhenAll(Members.Keys.Select(async id=>
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(400);
            // First visit starts with a current checkpoint, not a misleading replay of old traffic.
            var query=positions.TryGetValue(id,out var p)?$"?run={p.Run}&after={p.After}&take=500":$"?after={long.MaxValue}&take=500";
            try
            {
                var wire=await Inspect<TraceWire>(id,"/internal/trace"+query,timeout.Token);
                return new TraceNode(id,true,wire.Trace,wire.Checkpoint,null);
            }
            catch(Exception e) when(e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
            { return new TraceNode(id,false,null,null,"Нет актуального наблюдения узла"); }
        }));
        return new(DateTimeOffset.UtcNow,nodes);
    }
    public async Task Fault(int id,object fault,CancellationToken ct)
    { using var response=await http.PostAsJsonAsync(Members[id]+"/internal/fault",fault,ct); response.EnsureSuccessStatusCode(); }
}
