using Burmalda.Diagnostics;
using Burmalda.Raft;
using Burmalda.Web.Models;
using Xunit;

namespace Burmalda.Raft.Tests;

public sealed class VisualizationTests
{
    [Fact] public void Trace_is_bounded_correlated_and_detects_lost_ranges_and_restarts()
    {
        var buffer=new TraceBuffer(1,3);var rpc=Guid.NewGuid();
        buffer.Add("Sent","",1,2,rpc,"RequestVote",4);
        buffer.Add("Response","",2,1,rpc,"RequestVote",4,success:false);
        var first=buffer.Read(null,0);
        Assert.Equal(first.Entries[0].RpcId,first.Entries[1].RpcId);
        Assert.False(first.Entries[1].Success);
        buffer.Add("Timeout","");buffer.Add("Dropped","");buffer.Add("Blocked","");
        var page=buffer.Read(buffer.RunId,1);
        Assert.True(page.Gap);Assert.Equal(3,page.Entries.Length);Assert.Equal(5,page.Cursor);
        Assert.Empty(buffer.Read(buffer.RunId,page.Cursor).Entries);
        Assert.True(buffer.Read(Guid.NewGuid(),999).Gap);
        Assert.Empty(buffer.Read(null,long.MaxValue).Entries);
        Assert.Equal(5,buffer.Read(null,long.MaxValue).Cursor);
    }
    [Fact] public void Diagnostics_report_own_vote_and_real_replication_matches_without_fabricating_commit()
    {
        var s=new Simulation();s.Now=250;
        s.Nodes[1].Tick(s.Now);
        var candidate=s.Nodes[1].TraceState();
        Assert.Equal("Candidate",candidate.Role);Assert.Equal(new[]{1},candidate.Votes);
        s.Pump();s.Advance();var leader=s.Leader;
        var before=leader.CommitIndex;s.Partition(leader.Status().NodeId,5==leader.Status().NodeId?4:5);
        leader.Propose(new(Guid.NewGuid(),"minority"));s.Pump();
        var isolated=leader.TraceState();
        Assert.Equal(before,isolated.CommitIndex);
        Assert.True(1+isolated.Match.Count(x=>x.Value>=isolated.LastIndex)<3);
        Assert.False(isolated.Log.Last().Committed);
    }
    private static TraceNode Node(long commit=1,bool authority=true)=>new(1,true,null,
        new(new(1,"Leader",1,1,commit,commit,commit,[1,2,3],new(){{2,commit},{3,commit}},[new(commit,1,true,true)]),[],authority),null);
    [Fact] public void Pause_freezes_checkpoint_while_collection_continues_and_rpc_is_not_duplicated()
    {
        var history=new RaftSceneHistory();var at=DateTimeOffset.UtcNow;
        history.Ingest(new(at,[Node()]));history.Select(0);
        var run=Guid.NewGuid();var e=new TraceEntry(run,1,at.AddMilliseconds(1),1,"Commit","",State:Node(2).Checkpoint!.State);
        var next=Node(2) with {Trace=new(run,1,false,[e])};
        history.Ingest(new(at.AddSeconds(1),[next]));
        Assert.Equal(1,history.Selected!.Nodes[0].Checkpoint!.State.CommitIndex);
        Assert.Equal(1,history.NewEvents);Assert.False(history.IsLive);
        history.Ingest(new(at.AddSeconds(2),[next]));
        Assert.Single(history.Items,x=>x.Event.Kind=="Commit");
        history.Live();Assert.Equal(2,history.Selected!.Nodes[0].Checkpoint!.State.CommitIndex);
    }
    [Fact] public void Retention_repositions_paused_cursor_and_records_restart_gaps()
    {
        var history=new RaftSceneHistory();var at=DateTimeOffset.UtcNow;
        history.Ingest(new(at,[Node()]));history.Select(0);
        var run=Guid.NewGuid();
        history.Ingest(new(at.AddMinutes(6),[Node() with {Trace=new(run,0,true,[])}]));
        Assert.Single(history.Items);Assert.Equal(0,history.Position);Assert.NotNull(history.Notice);
        Assert.Contains("вышел",history.Notice);
    }
    [Fact] public void Twenty_thousand_event_limit_holds_even_with_dense_traffic()
    {
        var history=new RaftSceneHistory();var at=DateTimeOffset.UtcNow;var run=Guid.NewGuid();
        history.Ingest(new(at,[Node()]));
        var entries=Enumerable.Range(1,21000).Select(i=>new TraceEntry(run,i,at,1,"Sent","",1,2)).ToArray();
        history.Ingest(new(at,[Node() with {Trace=new(run,21000,false,entries)}]));
        Assert.Equal(20000,history.Items.Count);Assert.Equal(19999,history.Position);
    }
}
