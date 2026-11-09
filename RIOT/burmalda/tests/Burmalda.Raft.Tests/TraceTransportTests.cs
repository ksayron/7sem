using System.Net;
using System.Net.Http.Json;
using Burmalda.ClusterControl;
using Burmalda.PostgresControl;
using Burmalda.Raft;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Burmalda.Raft.Tests;

public sealed class TraceTransportTests
{
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted=>CancellationToken.None;
        public CancellationToken ApplicationStopping=>CancellationToken.None;
        public CancellationToken ApplicationStopped=>CancellationToken.None;
        public void StopApplication() { }
    }
    private sealed class Handler(string mode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Assert.True(Guid.TryParse(request.Headers.GetValues("X-Raft-Rpc").Single(),out _));
            if(mode=="timeout")throw new TaskCanceledException();
            if(mode=="unreachable")throw new HttpRequestException("Offline");
            if(mode=="reject")return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(new VoteResponse(1,false))});
        }
    }
    private static NodeHost Host(string mode)=>new(new(1,"trace-tests",Enumerable.Range(1,5).ToDictionary(i=>i,i=>$"http://node-{i}")),new MemoryStorage(),new ControlStateMachine(),new ConfigurationBuilder().Build(),new Lifetime(),NullLogger<NodeHost>.Instance,new Handler(mode));
    [Theory]
    [InlineData("deny","Response")]
    [InlineData("reject","Rejected")]
    [InlineData("timeout","Timeout")]
    [InlineData("unreachable","TransportError")]
    public async Task Rpc_outcomes_preserve_request_identity_and_do_not_invent_votes(string mode,string outcome)
    {
        using var host=Host(mode);
        await host.Serial(n=>{n.Tick(10000);return true;});
        var entries=host.Trace.Read(null,0).Entries;
        Assert.Equal(4,entries.Count(e=>e.Kind==outcome));
        foreach(var response in entries.Where(e=>e.Kind==outcome))
        {
            Assert.Single(entries,e=>e.Kind=="Sent" && e.RpcId==response.RpcId);
            Assert.False(response.Success);
        }
        Assert.Equal("Candidate",host.Core.TraceState().Role);
        Assert.Equal(new[]{1},host.Core.TraceState().Votes);
    }
    [Theory]
    [InlineData(true,"Blocked")]
    [InlineData(false,"Dropped")]
    public async Task Faults_are_traced_without_sending_rpc(bool partition,string outcome)
    {
        using var host=Host("deny");
        host.SetFault(partition?new([2,3,4,5]):new([],DropEvery:1));
        await host.Serial(n=>{n.Tick(10000);return true;});
        var entries=host.Trace.Read(null,0).Entries;
        Assert.Equal(4,entries.Count(e=>e.Kind==outcome));
        Assert.DoesNotContain(entries,e=>e.Kind=="Sent");
    }
}
