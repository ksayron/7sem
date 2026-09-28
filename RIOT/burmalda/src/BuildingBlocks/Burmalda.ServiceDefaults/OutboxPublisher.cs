using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Burmalda.ServiceDefaults;

public sealed class OutboxPublisher<TDbContext>(
    IServiceScopeFactory scopeFactory,
    RabbitMqConnection rabbitMq,
    ILogger<OutboxPublisher<TDbContext>> logger) : BackgroundService
    where TDbContext : DbContext
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PublishBatch(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox publishing iteration failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private async Task PublishBatch(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var messages = await db.Set<OutboxMessage>()
            .Where(x => x.PublishedAt == null && x.RetryCount < 20)
            .OrderBy(x => x.OccurredAt)
            .Take(50)
            .ToListAsync(cancellationToken);
        if (messages.Count == 0)
            return;

        var connection = await rabbitMq.GetAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await RabbitTopology.DeclareExchanges(channel, cancellationToken);

        foreach (var message in messages)
        {
            using var activity = Telemetry.ActivitySource.StartActivity("outbox.publish", ActivityKind.Producer);
            activity?.SetTag("messaging.message.id", message.Id);
            activity?.SetTag("messaging.destination.name", message.Exchange);
            try
            {
                var properties = new BasicProperties
                {
                    Persistent = true,
                    MessageId = message.Id.ToString(),
                    Type = message.Type,
                    ContentType = "application/json"
                };
                await channel.BasicPublishAsync(
                    message.Exchange,
                    message.RoutingKey,
                    mandatory: true,
                    basicProperties: properties,
                    body: Encoding.UTF8.GetBytes(message.Payload),
                    cancellationToken);
                message.PublishedAt = DateTimeOffset.UtcNow;
                message.LastError = null;
            }
            catch (Exception ex)
            {
                message.RetryCount++;
                message.LastError = ex.Message[..Math.Min(ex.Message.Length, 2000)];
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            }
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}
