using RabbitMQ.Client;

namespace Burmalda.ServiceDefaults;

public static class RabbitTopology
{
    public const string RetryExchange = "burmalda.retry";
    public const string DeadExchange = "burmalda.dead";

    public static async Task DeclareExchanges(IChannel channel, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(Outbox.EventsExchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(Outbox.CommandsExchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(RetryExchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(DeadExchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: cancellationToken);
    }

    public static async Task DeclareQueue(IChannel channel, IMessageQueueDefinition definition, CancellationToken cancellationToken)
    {
        await DeclareExchanges(channel, cancellationToken);
        var retryQueue = $"{definition.QueueName}.retry";
        var deadQueue = $"{definition.QueueName}.dead";
        var mainArgs = new Dictionary<string, object?>
        {
            ["x-dead-letter-exchange"] = RetryExchange,
            ["x-dead-letter-routing-key"] = retryQueue
        };
        var retryArgs = new Dictionary<string, object?>
        {
            ["x-message-ttl"] = 5000,
            ["x-dead-letter-exchange"] = string.Empty,
            ["x-dead-letter-routing-key"] = definition.QueueName
        };
        await channel.QueueDeclareAsync(definition.QueueName, durable: true, exclusive: false, autoDelete: false, arguments: mainArgs, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(retryQueue, durable: true, exclusive: false, autoDelete: false, arguments: retryArgs, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(deadQueue, durable: true, exclusive: false, autoDelete: false, arguments: null, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(retryQueue, RetryExchange, retryQueue, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(deadQueue, DeadExchange, deadQueue, cancellationToken: cancellationToken);
        foreach (var binding in definition.Bindings)
            await channel.QueueBindAsync(definition.QueueName, binding.Exchange, binding.RoutingKey, cancellationToken: cancellationToken);
    }
}
