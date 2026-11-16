using Burmalda.DemoControl;
using Xunit;

namespace Burmalda.DemoControl.Tests;

public sealed class ReleaseTests
{
    [Fact]
    public void ReleaseScenarioKeepsTrafficForAFullPipelineWithoutInjectingRecoveryFaults()
    {
        var options = new RunOptions("release", 60, 30, 1200);
        options.Validate();
        Assert.Equal(60, ScenarioSchedule.Target(options, 900));
        Assert.Equal(60, ScenarioSchedule.Target(options, 1259));
        Assert.Throws<DemoConflict>(() => new RunOptions("mixed", 60, 30, 1200).Validate());
    }

    [Fact]
    public void ReleaseProofCountsErrorsAndBothRevisionsOnlyInsideTheMeasuredWindow()
    {
        var traffic = new ReleaseTraffic();
        var start = DateTimeOffset.UtcNow.AddSeconds(1);
        traffic.Record(start.AddSeconds(-1), 1, "server_error", "outside");
        traffic.Record(start.AddSeconds(1), 20, "success", "old");
        traffic.Record(start.AddSeconds(2), 30, "success", "new");
        traffic.Record(start.AddSeconds(3), 40, "network_error", "");
        traffic.Record(start.AddSeconds(4), 50, "success", "outside");
        var proof = traffic.Read(start, start.AddSeconds(3));
        Assert.Equal(3, proof.Requests);
        Assert.Equal(1, proof.Errors);
        Assert.Equal(1, proof.Versions["old"]);
        Assert.Equal(1, proof.Versions["new"]);
        Assert.DoesNotContain("outside", proof.Versions.Keys);
        Assert.True(proof.Covered);
    }

    [Fact]
    public void EmptyAndLateObservationCannotBePresentedAsCompleteEvidence()
    {
        var traffic = new ReleaseTraffic();
        Assert.Null(traffic.Read(null, null).From);
        Assert.False(traffic.Read(DateTimeOffset.UtcNow.AddMinutes(-1), null).Covered);
        Assert.Equal(0, traffic.Read(DateTimeOffset.UtcNow, null).Requests);
    }
}
