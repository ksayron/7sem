using System.Diagnostics;
using System.Text.Json;

namespace Burmalda.Contracts;

public sealed record MessageEnvelope(
    Guid MessageId,
    Guid CorrelationId,
    Guid? CausationId,
    string Type,
    DateTimeOffset OccurredAt,
    int Version,
    JsonElement Payload,
    string? TraceParent,
    string? TraceState)
{
    public static MessageEnvelope Create<T>(
        T payload,
        Guid correlationId,
        Guid? causationId = null,
        Guid? messageId = null,
        DateTimeOffset? occurredAt = null)
    {
        var activity = Activity.Current;
        return new MessageEnvelope(
            messageId ?? Guid.NewGuid(),
            correlationId,
            causationId,
            typeof(T).Name,
            occurredAt ?? DateTimeOffset.UtcNow,
            1,
            JsonSerializer.SerializeToElement(payload, ContractJson.Options),
            activity?.Id,
            activity?.TraceStateString);
    }

    public T DeserializePayload<T>() =>
        Payload.Deserialize<T>(ContractJson.Options)
        ?? throw new JsonException($"Message {MessageId} payload is not {typeof(T).Name}.");
}

public static class ContractJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
