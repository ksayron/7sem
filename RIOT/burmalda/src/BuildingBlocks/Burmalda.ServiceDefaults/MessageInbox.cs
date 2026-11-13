using Burmalda.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Burmalda.ServiceDefaults;

public static class MessageInbox
{
    public static async Task<bool> Process(DbContext db, IEnumerable<IIntegrationMessageHandler> availableHandlers,
        string queue, MessageEnvelope envelope, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await PostgresCoordination.LockTransaction(db, $"burmalda:inbox:{queue}:{envelope.MessageId}", ct);
        if (await db.Set<InboxMessage>().AnyAsync(x => x.MessageId == envelope.MessageId && x.Consumer == queue, ct))
        {
            await transaction.CommitAsync(ct);
            return false;
        }
        var handlers = availableHandlers.Where(x => x.MessageType == envelope.Type).ToArray();
        if (handlers.Length == 0) throw new InvalidOperationException($"No handler for {envelope.Type} in {queue}.");
        foreach (var handler in handlers) await handler.Handle(envelope, ct);
        db.Set<InboxMessage>().Add(new InboxMessage { MessageId = envelope.MessageId, Consumer = queue, ProcessedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
