using System.Diagnostics;
using Burmalda.Raft;
using Burmalda.PostgresControl;
using Burmalda.ClusterControl;
using Xunit;

namespace Burmalda.Raft.Tests;

public sealed class ControlTests
{
    [Fact] public void Equipment_journal_rejects_an_independent_raft_configuration()
    {
        var root=Path.Combine(Path.GetTempPath(),"raft-owner-"+Guid.NewGuid());
        try
        {
            var journal=new ExecutionJournal(root);using var gate=journal.TryLock();
            journal.BindCluster("cluster-A");journal.BindCluster("cluster-A");
            Assert.Throws<InvalidOperationException>(()=>journal.BindCluster("cluster-B"));
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact] public void Invalid_application_command_is_rejected_deterministically()
    {
        var machine=new ControlStateMachine();
        Assert.False(RaftJson.Read<ControlAck>(machine.Apply("null")).Accepted);
        Assert.False(RaftJson.Read<ControlAck>(machine.Apply("{broken")).Accepted);
        Assert.Throws<ArgumentException>(()=>machine.Restore("null"));
        Assert.False(machine.State.Initialized);
    }
    [Fact] public void Restarting_promoted_primary_preserves_fence_of_former_primary()
    {
        var machine=new ControlStateMachine();
        machine.Restore(RaftJson.Write(new ClusterState { Initialized=true,Primary="postgres-replica",FencedSource="postgres",RequiredLsn="0/42",FenceOperation=Guid.NewGuid() }));
        var id=Guid.NewGuid();machine.Apply(RaftJson.Write(new ControlEntry("Begin",id,new(id,"Recover","postgres-replica"))));
        machine.Apply(RaftJson.Write(new ControlEntry("Stage",id,Stage:"Planned",Source:"postgres-replica",Target:"postgres-replica")));
        machine.Apply(RaftJson.Write(new ControlEntry("Stage",id,Stage:"Recovered")));
        Assert.Equal("postgres",machine.State.FencedSource);Assert.Equal("0/42",machine.State.RequiredLsn);
    }
    [Fact] public void State_machine_serializes_operations_and_preserves_fencing_across_leadership_changes()
    {
        var machine=new ControlStateMachine();
        ControlAck Apply(ControlEntry e)=>RaftJson.Read<ControlAck>(machine.Apply(RaftJson.Write(e)));
        Assert.False(Apply(new("Begin",Guid.NewGuid(),new(Guid.NewGuid(),"Switchover"))).Accepted);
        Assert.True(Apply(new("Bootstrap",Guid.Empty,Value:"postgres")).Accepted);
        var id=Guid.NewGuid(); var request=new OperationRequest(id,"Switchover");
        Assert.True(Apply(new("Begin",id,request)).Accepted);
        var other=Guid.NewGuid(); Assert.False(Apply(new("Begin",other,new(other,"Recover","postgres"))).Accepted);
        Assert.True(Apply(new("Stage",id,Stage:"Planned",Source:"postgres",Target:"postgres-replica",ExpectedStage:"Begin")).Accepted);
        Assert.False(Apply(new("Stage",id,Stage:"PrimaryActivated",ExpectedStage:"Begin")).Accepted);
        Assert.True(Apply(new("Stage",id,Stage:"SourceFenced",Value:"0/123",ExpectedStage:"Planned")).Accepted);
        var restarted=new ControlStateMachine(); restarted.Restore(machine.Export());
        Assert.Equal("postgres",restarted.State.FencedSource); Assert.Equal("0/123",restarted.State.RequiredLsn);
        Assert.Equal(id,restarted.State.ActiveOperation);
    }
    [Fact] public void Needs_inspection_blocks_following_operations()
    {
        var m=new ControlStateMachine(); m.Apply(RaftJson.Write(new ControlEntry("Bootstrap",Guid.Empty,Value:"postgres")));
        var id=Guid.NewGuid(); m.Apply(RaftJson.Write(new ControlEntry("Begin",id,new(id,"Switchover"))));
        m.Apply(RaftJson.Write(new ControlEntry("NeedsInspection",id,Value:"unknown Docker result")));
        var next=Guid.NewGuid(); var result=RaftJson.Read<ControlAck>(m.Apply(RaftJson.Write(new ControlEntry("Begin",next,new(next,"Recover","postgres")))));
        Assert.False(result.Accepted); Assert.Equal(id,m.State.ActiveOperation);
    }
    [Fact] public async Task Completed_external_action_is_not_repeated_after_reopening_journal()
    {
        var root=Path.Combine(Path.GetTempPath(),"raft-effects-"+Guid.NewGuid()); var id=Guid.NewGuid(); var calls=0;
        try
        {
            var journal=new ExecutionJournal(root);
            using(var gate=journal.TryLock())
            {
                Assert.NotNull(gate); Assert.Null(journal.TryLock());
                Assert.Equal("WAL",await journal.Run(id,"Fence",()=> {calls++;return Task.FromResult("WAL");},()=>Task.FromResult<string?>(null)));
            }
            var reopened=new ExecutionJournal(root); using var nextGate=reopened.TryLock();
            Assert.Equal("WAL",await reopened.Run(id,"Fence",()=> {calls++;return Task.FromResult("wrong");},()=>Task.FromResult<string?>(null)));
            Assert.Equal(1,calls);
        }
        finally { Directory.Delete(root,true); }
    }
    [Fact] public async Task Unknown_effect_is_blocked_until_observation_proves_its_result()
    {
        var root=Path.Combine(Path.GetTempPath(),"raft-effects-"+Guid.NewGuid()); var id=Guid.NewGuid();var calls=0;
        try
        {
            var journal=new ExecutionJournal(root); using var gate=journal.TryLock();
            Task<string> Crash(){calls++;throw new IOException("Lost acknowledgment");}
            await Assert.ThrowsAsync<NeedsInspectionException>(()=>journal.Run(id,"Promote",Crash,()=>Task.FromResult<string?>(null)));
            Assert.True(journal.HasUnresolved(id));
            await Assert.ThrowsAsync<NeedsInspectionException>(()=>journal.Run(id,"Promote",Crash,()=>Task.FromResult<string?>(null)));
            Assert.Equal("postgres-replica",await journal.Run(id,"Promote",Crash,()=>Task.FromResult<string?>("postgres-replica")));
            Assert.False(journal.HasUnresolved(id));
            Assert.Equal(1,calls);
        }
        finally { Directory.Delete(root,true); }
    }
    [Fact] public async Task Surviving_child_prevents_reconciliation_and_new_effect()
    {
        var root=Path.Combine(Path.GetTempPath(),"raft-effects-"+Guid.NewGuid());var id=Guid.NewGuid();
        try
        {
            var journal=new ExecutionJournal(root);using var gate=journal.TryLock();
            Task<string> Crash()
            {
                using var current=Process.GetCurrentProcess();
                ExecutionScope.Current!.Record(new(current.Id,current.StartTime.ToUniversalTime(),"test-child","same-process"));
                throw new IOException("Parent disappeared");
            }
            await Assert.ThrowsAsync<NeedsInspectionException>(()=>journal.Run(id,"Recover",Crash,()=>Task.FromResult<string?>(null)));
            await Assert.ThrowsAsync<ExternalActionRunningException>(()=>journal.Run(id,"Recover",Crash,()=>Task.FromResult<string?>("done")));
        }
        finally { Directory.Delete(root,true); }
    }
    [Theory] [InlineData("1/0","0/FFFFFFFF",true)] [InlineData("0/F","0/10",false)] [InlineData(null,"0/10",false)]
    public void Wal_guard_compares_numeric_positions(string? replay,string required,bool expected)
        =>Assert.Equal(expected,PostgresWorker.Replayed(replay,required));
    [Fact] public void Higher_term_response_demotes_even_if_it_is_from_an_old_request()
    {
        var s=new Simulation();s.Advance();var leader=s.Leader;
        leader.ReceiveAppend(leader.Status().NodeId==5?4:5,-1,new(1,leader.Status().NodeId,0,0,[],0),new(leader.Term+10,false,0,1,0));
        Assert.Equal(RaftRole.Follower,leader.Role);
    }
    [Fact] public void Committed_prefix_cannot_be_truncated_by_append_entries()
    {
        var s=new Simulation();s.Advance();var command=s.Commit("fixed");var node=s.Nodes[5];
        var index=node.CommitIndex;var response=node.AppendEntries(new(node.Term+1,1,index-1,node.TermAt(index-1),[new(index,node.Term+1,new(Guid.NewGuid(),"overwrite"))],index));
        Assert.False(response.Success);Assert.NotNull(node.Result(command.CommandId));Assert.DoesNotContain("overwrite",s.Machines[5].Values);
    }
    [Fact] public void Malformed_messages_and_snapshot_do_not_destroy_applied_state()
    {
        var s=new Simulation();s.Advance();var command=s.Commit("preserve");var node=s.Nodes[5];var index=node.LastIndex;
        Assert.Throws<ArgumentException>(()=>node.AppendEntries(new(node.Term+1,1,0,0,null!,0)));
        Assert.Throws<ArgumentException>(()=>node.InstallSnapshot(new(node.Term+1,1,new(index+10,node.Term,"{}",[]))));
        Assert.Equal(index,node.LastIndex);Assert.NotNull(node.Result(command.CommandId));Assert.Contains("preserve",s.Machines[5].Values);
    }
}
