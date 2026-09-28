using Burmalda.Contracts;
using Burmalda.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

namespace Burmalda.Lending.Api;

public sealed class LendingQueue : IMessageQueueDefinition
{
    public string QueueName => "lending.loan-request-saga";
    public IReadOnlyCollection<QueueBinding> Bindings =>
    [
        new(Outbox.EventsExchange, Routing.Event<BookCopyReserved>()),
        new(Outbox.EventsExchange, Routing.Event<BookCopyReservationFailed>()),
        new(Outbox.EventsExchange, Routing.Event<BookCopyReleased>()),
        new(Outbox.EventsExchange, Routing.Event<PointsReserved>()),
        new(Outbox.EventsExchange, Routing.Event<PointsReservationFailed>())
    ];
}

public sealed class LendingMessageHandler(LendingDbContext db, string messageType) : IIntegrationMessageHandler
{
    public string MessageType => messageType;
    public async Task Handle(MessageEnvelope envelope, CancellationToken ct)
    {
        switch (envelope.Type)
        {
            case MessageNames.BookCopyReserved:
            {
                var evt = envelope.DeserializePayload<BookCopyReserved>(); var saga = await db.Sagas.SingleAsync(x => x.Id == evt.SagaId, ct);
                if (saga.State != LoanSagaState.AwaitingCopyReservation) return;
                saga.OwnerId = evt.OwnerId; saga.Price = evt.LendingPrice; saga.MaximumLoanDays = evt.MaximumLoanDays; saga.State = LoanSagaState.AwaitingPointsReservation; saga.UpdatedAt = DateTimeOffset.UtcNow;
                Outbox.AddCommand(db, "economy", new ReservePoints(saga.Id, saga.BorrowerId, evt.LendingPrice, saga.Deadline), saga.Id, envelope.MessageId); break;
            }
            case MessageNames.BookCopyReservationFailed:
            {
                var evt = envelope.DeserializePayload<BookCopyReservationFailed>(); var saga = await db.Sagas.SingleAsync(x => x.Id == evt.SagaId, ct);
                if (saga.State != LoanSagaState.AwaitingCopyReservation) return;
                saga.State = LoanSagaState.Failed; saga.FailureReason = evt.Reason; saga.UpdatedAt = DateTimeOffset.UtcNow; break;
            }
            case MessageNames.PointsReserved:
            {
                var evt = envelope.DeserializePayload<PointsReserved>(); var saga = await db.Sagas.SingleAsync(x => x.Id == evt.SagaId, ct);
                if (saga.State != LoanSagaState.AwaitingPointsReservation) { Outbox.AddCommand(db, "economy", new ReleasePoints(evt.SagaId, evt.ReservationId, "Saga is no longer active."), evt.SagaId, envelope.MessageId); return; }
                var now = DateTimeOffset.UtcNow; var loan = new Loan
                {
                    Id = Guid.NewGuid(), SagaId = saga.Id, CopyId = saga.CopyId, BorrowerId = saga.BorrowerId, OwnerId = saga.OwnerId!.Value, ReservationId = evt.ReservationId,
                    Price = saga.Price!.Value, MaximumLoanDays = saga.MaximumLoanDays!.Value, Status = LoanStatus.Requested, Version = 1, CreatedAt = now, UpdatedAt = now
                };
                db.Loans.Add(loan); LoanHistory.Add(db, loan, saga.BorrowerId, "Distributed reservations completed; request created.");
                saga.LoanId = loan.Id; saga.PointsReservationId = evt.ReservationId; saga.State = LoanSagaState.Completed; saga.UpdatedAt = now;
                Outbox.AddEvent(db, new LoanRequested(loan.Id, saga.Id, loan.CopyId, loan.BorrowerId, loan.OwnerId, loan.Price), saga.Id, envelope.MessageId); break;
            }
            case MessageNames.PointsReservationFailed:
            {
                var evt = envelope.DeserializePayload<PointsReservationFailed>(); var saga = await db.Sagas.SingleAsync(x => x.Id == evt.SagaId, ct);
                if (saga.State != LoanSagaState.AwaitingPointsReservation) return;
                saga.State = LoanSagaState.CompensatingCopy; saga.FailureReason = evt.Reason; saga.UpdatedAt = DateTimeOffset.UtcNow;
                Outbox.AddCommand(db, "catalog", new ReleaseBookCopy(saga.Id, saga.CopyId, evt.Reason), saga.Id, envelope.MessageId); break;
            }
            case MessageNames.BookCopyReleased:
            {
                var evt = envelope.DeserializePayload<BookCopyReleased>(); var saga = await db.Sagas.SingleOrDefaultAsync(x => x.Id == evt.SagaId, ct);
                if (saga is not null && saga.State == LoanSagaState.CompensatingCopy) { saga.State = LoanSagaState.Failed; saga.UpdatedAt = DateTimeOffset.UtcNow; } break;
            }
        }
    }
}
