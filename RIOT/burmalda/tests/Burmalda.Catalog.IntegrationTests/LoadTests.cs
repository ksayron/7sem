using System.Diagnostics;
using System.Text.Json;
using Burmalda.Catalog.Api;
using Burmalda.Contracts;
using Burmalda.Persistence;
using Burmalda.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Burmalda.Catalog.IntegrationTests;

public sealed class LoadTests(ITestOutputHelper output)
{
    [PostgresFact, Trait("Category", "Load")]
    public async Task Eight_workers_reserve_and_release_without_losing_domain_effects()
    {
        const int workers = 8, iterations = 20;
        await using var database = await TestDatabase.Create();
        var fixtures = new List<(Guid book, Guid copy)>();
        for (var i = 0; i < workers; i++) fixtures.Add(await ReplicaTests.Seed(database));
        var clock = Stopwatch.StartNew();
        await Task.WhenAll(fixtures.Select(async fixture =>
        {
            for (var i = 0; i < iterations; i++)
            {
                var saga = Guid.NewGuid();
                await Process(MessageEnvelope.Create(new ReserveBookCopy(saga, fixture.copy, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5)), Guid.NewGuid()));
                await Process(MessageEnvelope.Create(new ReleaseBookCopy(saga, fixture.copy, "CI load check"), Guid.NewGuid()));
            }
        }));
        clock.Stop();
        await using var check = database.Context();
        Assert.Equal(workers, await check.BookCopies.CountAsync(x => x.Status == BookCopyStatus.Available));
        Assert.Equal(workers, await check.SearchProjections.CountAsync(x => x.AvailableCopies == 1));
        Assert.Equal(workers * iterations * 2, await check.Set<InboxMessage>().CountAsync());
        Assert.Equal(workers * iterations, await check.Set<OutboxMessage>().CountAsync(x => x.Type == MessageNames.BookCopyReserved));
        Assert.Equal(0, await check.Set<OutboxMessage>().CountAsync(x => x.Type == MessageNames.BookCopyReservationFailed));
        var report = new { workers, iterations, operations = workers * iterations * 2, elapsedMs = clock.ElapsedMilliseconds, success = true };
        output.WriteLine(JsonSerializer.Serialize(report));
        if (Environment.GetEnvironmentVariable("BURMALDA_LOAD_REPORT") is { Length: > 0 } path)
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report));

        async Task Process(MessageEnvelope envelope)
        {
            await using var db = database.Context();
            Assert.True(await MessageInbox.Process(db, [new CatalogMessageHandler(db, envelope.Type)], "ci-load", envelope));
        }
    }
}
