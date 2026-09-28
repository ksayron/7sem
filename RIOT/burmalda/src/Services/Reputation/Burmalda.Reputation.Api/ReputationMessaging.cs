using Burmalda.Contracts;
using Burmalda.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

namespace Burmalda.Reputation.Api;

public sealed class ReputationQueue : IMessageQueueDefinition
{
    public string QueueName => "reputation.loan-events";
    public IReadOnlyCollection<QueueBinding> Bindings
    => [
        new(Outbox.EventsExchange, Routing.Event<UserRegistered>()),
        new(Outbox.EventsExchange, Routing.Event<LoanCompleted>()),
        new(Outbox.EventsExchange, Routing.Event<LoanBecameOverdue>()),
        new(Outbox.EventsExchange, Routing.Event<LoanCancelled>())
    ];
}

public sealed class ReputationMessageHandler(ReputationDbContext db, string messageType) : IIntegrationMessageHandler
{
    public string MessageType => messageType;
    public async Task Handle(MessageEnvelope envelope, CancellationToken ct)
    {
        switch (envelope.Type)
        {
            case MessageNames.UserRegistered:
            {
                var evt = envelope.DeserializePayload<UserRegistered>();
                if (!await db.Reputations.AnyAsync(x => x.UserId == evt.UserId, ct)) db.Reputations.Add(new UserReputation { UserId = evt.UserId, Score = 50, UpdatedAt = DateTimeOffset.UtcNow, Version = 1 });
                break;
            }
            case MessageNames.LoanCompleted:
            {
                var evt = envelope.DeserializePayload<LoanCompleted>(); await Apply(evt.OwnerId, 5, $"completed-owner:{evt.LoanId}", x => x.SuccessfulLoans++, ct);
                await Apply(evt.BorrowerId, evt.ReturnedOnTime ? 4 : 1, $"completed-borrower:{evt.LoanId}", x => x.SuccessfulReturns++, ct); break;
            }
            case MessageNames.LoanBecameOverdue:
            {
                var evt = envelope.DeserializePayload<LoanBecameOverdue>(); await Apply(evt.BorrowerId, -10, $"overdue:{evt.LoanId}", x => x.OverdueLoans++, ct); break;
            }
            case MessageNames.LoanCancelled:
            {
                var evt = envelope.DeserializePayload<LoanCancelled>(); await Apply(evt.BorrowerId, -2, $"cancelled:{evt.LoanId}", x => x.CancelledLoans++, ct); break;
            }
        }
    }

    private async Task Apply(Guid userId, int delta, string key, Action<UserReputation> update, CancellationToken ct)
    {
        if (await db.Adjustments.AnyAsync(x => x.IdempotencyKey == key, ct)) return;
        var value = await db.Reputations.SingleOrDefaultAsync(x => x.UserId == userId, ct) ?? new UserReputation { UserId = userId, Score = 50, Version = 1 };
        if (db.Entry(value).State == EntityState.Detached) db.Reputations.Add(value);
        update(value); value.Score = Math.Clamp(value.Score + delta, 0, 1000); value.UpdatedAt = DateTimeOffset.UtcNow; value.Version++;
        db.Adjustments.Add(new ReputationAdjustment { Id = Guid.NewGuid(), UserId = userId, Delta = delta, Reason = key, IdempotencyKey = key, CreatedAt = DateTimeOffset.UtcNow });
    }
}
