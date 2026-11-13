using System.Text.Json;
using Burmalda.Catalog.Api;
using Burmalda.Contracts;
using Burmalda.Persistence;
using Burmalda.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using RabbitMQ.Client;
using Xunit;

namespace Burmalda.Catalog.IntegrationTests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BURMALDA_TEST_POSTGRES")))
            Skip = "Run tools/Test-CatalogReplicas.ps1 against the local core PostgreSQL and RabbitMQ.";
    }
}

public sealed class ReplicaTests
{
    [PostgresFact]
    public async Task Three_instances_bootstrap_one_empty_database()
    {
        await using var database = await TestDatabase.Create();
        await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddDbContext<CatalogDbContext>(o => o.UseNpgsql(database.Connection).WithDatabaseFailover());
            await using var app = builder.Build();
            await app.EnsureDatabaseAsync<CatalogDbContext>();
        }));
        await using var db = database.Context();
        Assert.Equal(0, await db.Books.CountAsync());
        Assert.Equal(0, await db.Set<OutboxMessage>().CountAsync());
    }

    [PostgresFact]
    public async Task Concurrent_redelivery_commits_one_inbox_and_one_domain_effect()
    {
        await using var database = await TestDatabase.Create();
        var fixture = await Seed(database);
        var envelope = MessageEnvelope.Create(new ReserveBookCopy(Guid.NewGuid(), fixture.copy, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5)), Guid.NewGuid());
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            await using var db = database.Context();
            return await MessageInbox.Process(db, [new CatalogMessageHandler(db, envelope.Type)], "test-catalog", envelope);
        }));
        Assert.Single(results, x => x);
        await using var check = database.Context();
        Assert.Equal(1, await check.Set<InboxMessage>().CountAsync());
        Assert.Equal(1, await check.Set<OutboxMessage>().CountAsync());
        Assert.Equal(BookCopyStatus.Reserved, (await check.BookCopies.SingleAsync()).Status);
        Assert.Equal(0, (await check.SearchProjections.SingleAsync()).AvailableCopies);
    }

    [PostgresFact]
    public async Task Competing_reservations_have_one_winner_and_return_refreshes_projection()
    {
        await using var database = await TestDatabase.Create();
        var fixture = await Seed(database);
        var envelopes = Enumerable.Range(0, 2).Select(_ => MessageEnvelope.Create(
            new ReserveBookCopy(Guid.NewGuid(), fixture.copy, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5)), Guid.NewGuid())).ToArray();
        await Task.WhenAll(envelopes.Select(async envelope =>
        {
            await using var db = database.Context();
            await MessageInbox.Process(db, [new CatalogMessageHandler(db, envelope.Type)], "test-catalog", envelope);
        }));
        await using var check = database.Context();
        Assert.Equal(1, await check.Set<OutboxMessage>().CountAsync(x => x.Type == MessageNames.BookCopyReserved));
        Assert.Equal(1, await check.Set<OutboxMessage>().CountAsync(x => x.Type == MessageNames.BookCopyReservationFailed));
        var copy = await check.BookCopies.AsNoTracking().SingleAsync();
        var release = MessageEnvelope.Create(new ReleaseBookCopy(copy.ReservationSagaId!.Value, copy.Id, "Replica test"), Guid.NewGuid());
        await using (var db = database.Context())
            await MessageInbox.Process(db, [new CatalogMessageHandler(db, release.Type)], "test-catalog", release);
        Assert.Equal(1, (await check.SearchProjections.AsNoTracking().SingleAsync()).AvailableCopies);
        Assert.Equal(BookCopyStatus.Available, (await check.BookCopies.AsNoTracking().SingleAsync()).Status);
    }

    [PostgresFact]
    public async Task Three_outbox_publishers_do_not_publish_the_same_batch_concurrently()
    {
        await using var database = await TestDatabase.Create();
        await using (var db = database.Context()) await db.Database.EnsureCreatedAsync();
        var options = new RabbitMqOptions
        {
            Host = "localhost", Port = int.Parse(Environment.GetEnvironmentVariable("BURMALDA_TEST_RABBIT_PORT") ?? "5672"),
            UserName = Environment.GetEnvironmentVariable("BURMALDA_TEST_RABBIT_USER")!,
            Password = Environment.GetEnvironmentVariable("BURMALDA_TEST_RABBIT_PASSWORD")!
        };
        await using var rabbit = new RabbitMqConnection(Options.Create(options));
        var connection = await rabbit.GetAsync(default);
        await using var channel = await connection.CreateChannelAsync();
        await RabbitTopology.DeclareExchanges(channel, default);
        var queue = await channel.QueueDeclareAsync(queue: "", durable: false, exclusive: true, autoDelete: true);
        var route = "lr9.test." + Guid.NewGuid();
        await channel.QueueBindAsync(queue.QueueName, Outbox.EventsExchange, route);
        await using (var db = database.Context())
        {
            for (var i = 0; i < 25; i++)
            {
                var envelope = MessageEnvelope.Create(new { Number = i }, Guid.NewGuid());
                db.Set<OutboxMessage>().Add(new OutboxMessage
                {
                    Id = envelope.MessageId, Exchange = Outbox.EventsExchange, RoutingKey = route, Type = envelope.Type,
                    Payload = JsonSerializer.Serialize(envelope, ContractJson.Options), OccurredAt = envelope.OccurredAt
                });
            }
            await db.SaveChangesAsync();
        }
        await using var services = new ServiceCollection().AddDbContext<CatalogDbContext>(o => o.UseNpgsql(database.Connection)).BuildServiceProvider();
        var workers = Enumerable.Range(0, 3).Select(_ => new OutboxPublisher<CatalogDbContext>(
            services.GetRequiredService<IServiceScopeFactory>(), rabbit, NullLogger<OutboxPublisher<CatalogDbContext>>.Instance)).ToArray();
        try
        {
            foreach (var worker in workers) await worker.StartAsync(default);
            var complete = false;
            for (var attempt = 0; attempt < 100 && !complete; attempt++)
            {
                await using var db = database.Context();
                complete = await db.Set<OutboxMessage>().CountAsync(x => x.PublishedAt != null) == 25;
                if (!complete) await Task.Delay(100);
            }
            Assert.True(complete, "Outbox did not finish within ten seconds.");
        }
        finally
        {
            foreach (var worker in workers) { await worker.StopAsync(default); worker.Dispose(); }
        }
        var ids = new HashSet<string>();
        while (await channel.BasicGetAsync(queue.QueueName, autoAck: true) is { } message)
            Assert.True(ids.Add(message.BasicProperties.MessageId!), "Duplicate publication by concurrent workers.");
        Assert.Equal(25, ids.Count);
    }

    private static async Task<(Guid book, Guid copy)> Seed(TestDatabase database)
    {
        await using var db = database.Context();
        await db.Database.EnsureCreatedAsync();
        var book = Guid.NewGuid(); var copy = Guid.NewGuid();
        db.Books.Add(new Book { Id = book, Isbn = book.ToString(), Title = "Replica test", AuthorsJson = "[\"Tester\"]", GenresJson = "[]", CreatedAt = DateTimeOffset.UtcNow });
        db.BookCopies.Add(new BookCopy { Id = copy, BookId = book, OwnerId = Guid.NewGuid(), Condition = BookCondition.Good, Status = BookCopyStatus.Available, LendingPrice = 10, MaximumLoanDays = 14, Location = "Test", Version = 1, CreatedAt = DateTimeOffset.UtcNow });
        db.SearchProjections.Add(new BookSearchProjection { BookId = book, Isbn = book.ToString(), Title = "Replica test", AuthorsText = "Tester", GenresText = "", AvailableCopies = 1, MinimumPrice = 10, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return (book, copy);
    }
}

internal sealed class TestDatabase(string admin, string name, string connection) : IAsyncDisposable
{
    public string Connection => connection;
    public CatalogDbContext Context() => new(new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(connection).Options);
    public static async Task<TestDatabase> Create()
    {
        var admin = Environment.GetEnvironmentVariable("BURMALDA_TEST_POSTGRES")!;
        var name = "lr9_test_" + Guid.NewGuid().ToString("N");
        await using var db = new NpgsqlConnection(admin); await db.OpenAsync();
        await new NpgsqlCommand($"CREATE DATABASE \"{name}\"", db).ExecuteNonQueryAsync();
        return new TestDatabase(admin, name, new NpgsqlConnectionStringBuilder(admin) { Database = name, Pooling = false }.ConnectionString);
    }
    public async ValueTask DisposeAsync()
    {
        if (!name.StartsWith("lr9_test_", StringComparison.Ordinal)) throw new InvalidOperationException("Refusing to remove a non-test database.");
        await using var db = new NpgsqlConnection(admin); await db.OpenAsync();
        await new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", db).ExecuteNonQueryAsync();
    }
}
