using System.Text.Json;
using Burmalda.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Burmalda.ServiceDefaults;

public static class Outbox
{
    public const string EventsExchange = "burmalda.events";
    public const string CommandsExchange = "burmalda.commands";

    public static void AddEvent<T>(DbContext db, T payload, Guid correlationId, Guid? causationId = null)
        => Add(db, EventsExchange, Routing.Event<T>(), payload, correlationId, causationId);

    public static void AddCommand<T>(DbContext db, string target, T payload, Guid correlationId, Guid? causationId = null)
        => Add(db, CommandsExchange, Routing.Command<T>(target), payload, correlationId, causationId);

    private static void Add<T>(DbContext db, string exchange, string routingKey, T payload, Guid correlationId, Guid? causationId)
    {
        var envelope = MessageEnvelope.Create(payload, correlationId, causationId);
        db.Set<OutboxMessage>().Add(new OutboxMessage
        {
            Id = envelope.MessageId,
            Exchange = exchange,
            RoutingKey = routingKey,
            Type = envelope.Type,
            Payload = JsonSerializer.Serialize(envelope, ContractJson.Options),
            OccurredAt = envelope.OccurredAt
        });
    }
}

public static class Routing
{
    public static string Event<T>() => $"event.{ToKebab(typeof(T).Name)}";
    public static string Command<T>(string target) => $"command.{target}.{ToKebab(typeof(T).Name)}";

    private static string ToKebab(string value) =>
        string.Concat(value.Select((c, i) => i > 0 && char.IsUpper(c) ? $"-{char.ToLowerInvariant(c)}" : char.ToLowerInvariant(c).ToString()));
}
