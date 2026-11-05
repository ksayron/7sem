using System.Net.Http.Json;
using Burmalda.Raft;
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
        var basePort = configuration.GetValue("Raft:BasePort", 5121);
        Members = Enumerable.Range(1,5).ToDictionary(x=>x,x=>$"http://127.0.0.1:{basePort+x-1}");
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
    public async Task Fault(int id,object fault,CancellationToken ct)
    { using var response=await http.PostAsJsonAsync(Members[id]+"/internal/fault",fault,ct); response.EnsureSuccessStatusCode(); }
}
