using Burmalda.ClusterControl;
using Burmalda.PostgresControl;
using System.Text.Json;
using Xunit;

namespace Burmalda.Raft.Tests;

public sealed class MaintenanceCleanupTests
{
    private static string Id(int value)=>value.ToString("x64");
    private static object Job(string id,Guid operation,string status="exited",int exit=0,
        string? name=null,string project="burmalda",bool oneoff=true)=>new
    {
        Id=id,Name=name??$"/burmalda-raft-maintenance-{operation:N}",
        State=new { Status=status,Running=status=="running",ExitCode=exit },
        Config=new { Labels=new Dictionary<string,string>
        {
            ["burmalda.raft.operation"]=operation.ToString(),["com.docker.compose.project"]=project,
            ["com.docker.compose.oneoff"]=oneoff?"True":"False"
        } }
    };

    [Fact] public async Task Cleanup_removes_only_successful_committed_owned_jobs_without_force_or_volumes()
    {
        var done=Guid.NewGuid();var active=Guid.NewGuid();var failed=Guid.NewGuid();
        var unknown=Guid.NewGuid();var wrongName=Guid.NewGuid();var otherProject=Guid.NewGuid();var database=Guid.NewGuid();
        object[] jobs=[Job(Id(1),done),Job(Id(2),active,"running"),Job(Id(3),failed,exit:1),
            Job(Id(4),unknown),Job(Id(5),wrongName,name:"/burmalda-postgres"),
            Job(Id(6),otherProject,project:"another-project"),Job(Id(7),database,oneoff:false)];
        var commands=new List<string[]>();
        Task<string> Docker(CancellationToken _,string[] args)
        {
            commands.Add(args);
            return Task.FromResult(args[0] switch
            {
                "ps"=>string.Join('\n',Enumerable.Range(1,7).Select(Id)),
                "inspect"=>JsonSerializer.Serialize(jobs),"rm"=>Id(1),_=>throw new Exception("Unexpected Docker command")
            });
        }
        var count=await MaintenanceCleanup.Run(Docker,new HashSet<Guid> {done,active,failed,wrongName,otherProject,database},default);
        Assert.Equal(1,count);
        Assert.Equal(new[]{"rm",Id(1)},Assert.Single(commands,args=>args[0]=="rm"));
        Assert.DoesNotContain(commands,args=>args.Contains("--force")||args.Contains("-f")||args.Contains("--volumes")||args.Contains("-v")||args.Contains("prune"));
    }

    [Fact] public async Task Empty_completed_set_does_not_contact_docker()
    {
        Assert.Equal(0,await MaintenanceCleanup.Run((_,_)=>throw new Exception("Must not contact Docker"),new HashSet<Guid>(),default));
    }

    [Fact] public async Task No_matching_containers_requires_no_inspection_or_removal()
    {
        var calls=0;
        Assert.Equal(0,await MaintenanceCleanup.Run((_,args)=>
        { calls++;Assert.Equal("ps",args[0]);return Task.FromResult(""); },new HashSet<Guid>{Guid.NewGuid()},default));
        Assert.Equal(1,calls);
    }

    [Fact] public async Task Failed_inspection_never_removes_anything()
    {
        var calls=new List<string>();
        await Assert.ThrowsAsync<IOException>(()=>MaintenanceCleanup.Run((_,args)=>
        {
            calls.Add(args[0]);if(args[0]=="inspect")throw new IOException("Docker unavailable");return Task.FromResult(Id(1));
        },new HashSet<Guid>{Guid.NewGuid()},default));
        Assert.DoesNotContain("rm",calls);
    }

    [Fact] public async Task Journal_requires_both_committed_completion_and_resolved_recovery()
    {
        var root=Path.Combine(Path.GetTempPath(),"raft-cleanup-"+Guid.NewGuid());
        try
        {
            var journal=new ExecutionJournal(root);using var gate=journal.TryLock();
            var done=Guid.NewGuid();var unresolved=Guid.NewGuid();var notCommitted=Guid.NewGuid();
            await journal.Run(done,"Recover",()=>Task.FromResult("Recovered"),()=>Task.FromResult<string?>(null));
            await journal.Run(notCommitted,"Recover",()=>Task.FromResult("Recovered"),()=>Task.FromResult<string?>(null));
            await Assert.ThrowsAsync<NeedsInspectionException>(()=>journal.Run(unresolved,"Recover",()=>throw new IOException("Unknown outcome"),()=>Task.FromResult<string?>(null)));
            Assert.Equal(new HashSet<Guid>{done},journal.CompletedRecoveries([done,unresolved,Guid.NewGuid()]));
        }
        finally { Directory.Delete(root,true); }
    }
}
