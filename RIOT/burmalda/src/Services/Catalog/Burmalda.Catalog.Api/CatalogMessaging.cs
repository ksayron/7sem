using Burmalda.Contracts;
using Burmalda.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

namespace Burmalda.Catalog.Api;

public sealed class CatalogQueue : IMessageQueueDefinition
{
    public string QueueName => "catalog.loan-workflow";
    public IReadOnlyCollection<QueueBinding> Bindings =>
    [
        new(Outbox.CommandsExchange, Routing.Command<ReserveBookCopy>("catalog")),
        new(Outbox.CommandsExchange, Routing.Command<ReleaseBookCopy>("catalog")),
        new(Outbox.EventsExchange, Routing.Event<LoanActivated>()),
        new(Outbox.EventsExchange, Routing.Event<LoanCompleted>())
    ];
}

public sealed class CatalogMessageHandler(CatalogDbContext db, string messageType) : IIntegrationMessageHandler
{
    public string MessageType => messageType;

    public async Task Handle(MessageEnvelope message, CancellationToken ct)
    {
        var copyId = message.Type switch
        {
            MessageNames.ReserveBookCopy => message.DeserializePayload<ReserveBookCopy>().CopyId,
            MessageNames.ReleaseBookCopy => message.DeserializePayload<ReleaseBookCopy>().CopyId,
            MessageNames.LoanActivated => message.DeserializePayload<LoanActivated>().CopyId,
            MessageNames.LoanCompleted => message.DeserializePayload<LoanCompleted>().CopyId,
            _ => throw new InvalidOperationException($"Unsupported message {message.Type}.")
        };
        await CatalogProjection.LockCopy(db, copyId, ct);
        switch (message.Type)
        {
            case MessageNames.ReserveBookCopy: await Reserve(message, ct); break;
            case MessageNames.ReleaseBookCopy: await Release(message, ct); break;
            case MessageNames.LoanActivated: await Activate(message, ct); break;
            case MessageNames.LoanCompleted: await Complete(message, ct); break;
            default: throw new InvalidOperationException($"Unsupported message {message.Type}.");
        }
    }

    private async Task Reserve(MessageEnvelope envelope, CancellationToken ct)
    {
        var command = envelope.DeserializePayload<ReserveBookCopy>();
        var updated = await db.BookCopies.Where(x => x.Id == command.CopyId && x.Status == BookCopyStatus.Available && !x.IsHidden && x.OwnerId != command.BorrowerId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, BookCopyStatus.Reserved)
                .SetProperty(x => x.ReservationSagaId, command.SagaId)
                .SetProperty(x => x.ReservedForUserId, command.BorrowerId)
                .SetProperty(x => x.ReservationExpiresAt, command.ExpiresAt)
                .SetProperty(x => x.Version, x => x.Version + 1), ct);
        if (updated == 1)
        {
            var copy = await db.BookCopies.AsNoTracking().SingleAsync(x => x.Id == command.CopyId, ct);
            Outbox.AddEvent(db, new BookCopyReserved(command.SagaId, copy.Id, copy.OwnerId, copy.LendingPrice, copy.MaximumLoanDays), envelope.CorrelationId, envelope.MessageId);
            await CatalogProjection.Refresh(db, copy.BookId, ct);
        }
        else Outbox.AddEvent(db, new BookCopyReservationFailed(command.SagaId, command.CopyId, "Copy is unavailable, hidden, or owned by the borrower."), envelope.CorrelationId, envelope.MessageId);
    }

    private async Task Release(MessageEnvelope envelope, CancellationToken ct)
    {
        var command = envelope.DeserializePayload<ReleaseBookCopy>();
        var copy = await db.BookCopies.SingleOrDefaultAsync(x => x.Id == command.CopyId && x.Status == BookCopyStatus.Reserved && x.ReservationSagaId == command.SagaId, ct);
        if (copy is null) return;
        copy.Status = BookCopyStatus.Available; copy.ReservationSagaId = null; copy.ReservedForUserId = null; copy.ReservationExpiresAt = null; copy.Version++;
        await CatalogProjection.Refresh(db, copy.BookId, ct);
        Outbox.AddEvent(db, new BookCopyReleased(command.SagaId, command.CopyId), envelope.CorrelationId, envelope.MessageId);
    }

    private async Task Activate(MessageEnvelope envelope, CancellationToken ct)
    {
        var evt = envelope.DeserializePayload<LoanActivated>();
        var copy = await db.BookCopies.SingleOrDefaultAsync(x => x.Id == evt.CopyId && x.ReservationSagaId == evt.SagaId, ct);
        if (copy is null) return;
        copy.Status = BookCopyStatus.OnLoan; copy.ReservationExpiresAt = null; copy.Version++;
        await CatalogProjection.Refresh(db, copy.BookId, ct);
    }

    private async Task Complete(MessageEnvelope envelope, CancellationToken ct)
    {
        var evt = envelope.DeserializePayload<LoanCompleted>();
        var copy = await db.BookCopies.SingleOrDefaultAsync(x => x.Id == evt.CopyId, ct);
        if (copy is null) return;
        copy.Status = BookCopyStatus.Available; copy.ReservationSagaId = null; copy.ReservedForUserId = null; copy.ReservationExpiresAt = null; copy.Version++;
        await CatalogProjection.Refresh(db, copy.BookId, ct);
    }
}
