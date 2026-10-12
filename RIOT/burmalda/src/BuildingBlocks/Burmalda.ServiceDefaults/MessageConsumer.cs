using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Burmalda.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Burmalda.ServiceDefaults;

public interface IIntegrationMessageHandler
{
    string MessageType { get; }
    Task Handle(MessageEnvelope message, CancellationToken cancellationToken);
}

public sealed class MessageConsumer<TDbContext>(
    IServiceScopeFactory scopeFactory,
    RabbitMqConnection rabbitMq,
    IMessageQueueDefinition definition,
    ILogger<MessageConsumer<TDbContext>> logger) : BackgroundService
    where TDbContext : DbContext
{
    private IChannel? _channel;
    private DateTimeOffset _lastErrorAt;
    private bool _unavailable;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var connection = await rabbitMq.GetAsync(stoppingToken);
                _channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await RabbitTopology.DeclareQueue(_channel, definition, stoppingToken);
                await _channel.BasicQosAsync(0, 8, false, stoppingToken);
                var consumer = new AsyncEventingBasicConsumer(_channel);
                consumer.ReceivedAsync += (_, args) => OnMessage(args, stoppingToken);
                await _channel.BasicConsumeAsync(definition.QueueName, autoAck: false, consumer, stoppingToken);
                if (_unavailable) logger.LogInformation("Consumer {Queue} connection restored", definition.QueueName);
                _unavailable = false;
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!_unavailable || DateTimeOffset.UtcNow - _lastErrorAt > TimeSpan.FromSeconds(30))
                {
                    logger.LogError(ex, "Consumer {Queue} unavailable; reconnecting", definition.QueueName);
                    _lastErrorAt = DateTimeOffset.UtcNow;
                }
                _unavailable = true;
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task OnMessage(BasicDeliverEventArgs args, CancellationToken cancellationToken)
    {
        if (_channel is null) return;
        MessageEnvelope? envelope = null;
        try
        {
            envelope = JsonSerializer.Deserialize<MessageEnvelope>(Encoding.UTF8.GetString(args.Body.Span), ContractJson.Options)
                ?? throw new JsonException("Message envelope was empty.");

            ActivityContext parent = default;
            if (!string.IsNullOrWhiteSpace(envelope.TraceParent))
                ActivityContext.TryParse(envelope.TraceParent, envelope.TraceState, out parent);
            using var activity = Telemetry.ActivitySource.StartActivity("message.consume", ActivityKind.Consumer, parent);
            activity?.SetTag("messaging.message.id", envelope.MessageId);
            activity?.SetTag("messaging.destination.name", definition.QueueName);

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
            var duplicate = await db.Set<InboxMessage>().AnyAsync(
                x => x.MessageId == envelope.MessageId && x.Consumer == definition.QueueName,
                cancellationToken);
            if (duplicate)
            {
                await _channel.BasicAckAsync(args.DeliveryTag, false, cancellationToken);
                return;
            }

            var handlers = scope.ServiceProvider.GetServices<IIntegrationMessageHandler>()
                .Where(x => x.MessageType == envelope.Type)
                .ToArray();
            if (handlers.Length == 0)
                throw new InvalidOperationException($"No handler for {envelope.Type} in {definition.QueueName}.");

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            foreach (var handler in handlers) await handler.Handle(envelope, cancellationToken);
            db.Set<InboxMessage>().Add(new InboxMessage
            {
                MessageId = envelope.MessageId,
                Consumer = definition.QueueName,
                ProcessedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("{Queue} ← {Event} · local COMMIT · correlation {Correlation}",
                definition.QueueName, envelope.Type, envelope.CorrelationId);
            await _channel.BasicAckAsync(args.DeliveryTag, false, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Message {MessageId} failed in {Queue}", envelope?.MessageId, definition.QueueName);
            if (GetRetryCount(args.BasicProperties.Headers) >= 3)
            {
                await _channel.BasicPublishAsync(
                    RabbitTopology.DeadExchange,
                    $"{definition.QueueName}.dead",
                    mandatory: false,
                    basicProperties: new BasicProperties { Persistent = true, Headers = args.BasicProperties.Headers },
                    body: args.Body,
                    cancellationToken);
                await _channel.BasicAckAsync(args.DeliveryTag, false, cancellationToken);
            }
            else
            {
                await _channel.BasicNackAsync(args.DeliveryTag, false, requeue: false, cancellationToken);
            }
        }
    }

    private static long GetRetryCount(IDictionary<string, object?>? headers)
    {
        if (headers is null || !headers.TryGetValue("x-death", out var value) || value is not IList<object?> deaths) return 0;
        return deaths.OfType<IDictionary<string, object?>>()
            .Select(x => x.TryGetValue("count", out var count) ? Convert.ToInt64(count) : 0)
            .DefaultIfEmpty(0)
            .Max();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_channel is not null) await _channel.DisposeAsync();
        await base.StopAsync(cancellationToken);
    }
}
