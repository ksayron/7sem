using Burmalda.ConcurrencyLab.Api;

namespace Burmalda.Domain.Tests;

public sealed class ConcurrencyLabTests
{
    [Fact]
    public async Task Sequential_unsafe_requests_have_no_lost_updates()
    {
        var experiment = new ReservationExperiment(new CreateExperimentRequest(ReservationMode.Unsafe, 20, 10, false));
        for (var i = 0; i < 20; i++)
        {
            Assert.True(experiment.TryAccept(out var number));
            await experiment.ReserveAsync(number, $"client-{i}", CancellationToken.None);
        }
        var result = experiment.Snapshot();
        Assert.Equal(20, result.ActualReservations);
        Assert.Equal(0, result.LostUpdates);
        Assert.Equal(1, result.MaxConcurrentRequests);
    }

    [Fact]
    public async Task Unsafe_mode_exposes_lost_updates()
    {
        var result = await Run(ReservationMode.Unsafe);
        Assert.Equal(20, result.CompletedRequests);
        Assert.True(result.MaxConcurrentRequests > 1);
        Assert.True(result.LostUpdates > 0);
    }

    [Theory]
    [InlineData(ReservationMode.Lock)]
    [InlineData(ReservationMode.SemaphoreSlim)]
    public async Task Critical_section_preserves_every_reservation(ReservationMode mode)
    {
        var result = await Run(mode);
        Assert.Equal(20, result.CompletedRequests);
        Assert.Equal(20, result.ActualReservations);
        Assert.Equal(0, result.LostUpdates);
    }

    private static async Task<ExperimentSnapshot> Run(ReservationMode mode)
    {
        var experiment = new ReservationExperiment(new CreateExperimentRequest(mode, 20, 40));
        var requests = Enumerable.Range(0, 20).Select(async i =>
        {
            Assert.True(experiment.TryAccept(out var number));
            await experiment.ReserveAsync(number, $"client-{i}", CancellationToken.None);
        });
        await Task.WhenAll(requests);
        return experiment.Snapshot();
    }
}
