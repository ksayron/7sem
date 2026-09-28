using Burmalda.Contracts;
using Burmalda.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

namespace Burmalda.Economy.Api;

public sealed class EconomyQueue : IMessageQueueDefinition
{
    public string QueueName => "economy.loan-events";
    public IReadOnlyCollection<QueueBinding> Bindings =>
    [
        new(Outbox.EventsExchange, Routing.Event<UserRegistered>()),
        new(Outbox.CommandsExchange, Routing.Command<ReservePoints>("economy")),
        new(Outbox.CommandsExchange, Routing.Command<ReleasePoints>("economy")),
        new(Outbox.EventsExchange, Routing.Event<LoanActivated>()),
        new(Outbox.EventsExchange, Routing.Event<LoanCompleted>()),
        new(Outbox.EventsExchange, Routing.Event<LoanBecameOverdue>())
    ];
}

public sealed class EconomyMessageHandler(EconomyDbContext db, string messageType) : IIntegrationMessageHandler
{
    public string MessageType => messageType;
    public async Task Handle(MessageEnvelope envelope, CancellationToken ct)
    {
        switch (envelope.Type)
        {
            case MessageNames.UserRegistered: await RegisterUser(envelope, ct); break;
            case MessageNames.ReservePoints: await Reserve(envelope, ct); break;
            case MessageNames.ReleasePoints: await Release(envelope, ct); break;
            case MessageNames.LoanActivated: await Capture(envelope, ct); break;
            case MessageNames.LoanCompleted: await Reward(envelope, ct); break;
            case MessageNames.LoanBecameOverdue: await Fine(envelope, ct); break;
        }
    }

    private async Task RegisterUser(MessageEnvelope envelope, CancellationToken ct)
    {
        var evt = envelope.DeserializePayload<UserRegistered>(); if (await db.Wallets.AnyAsync(x => x.UserId == evt.UserId, ct)) return;
        var wallet = new Wallet { UserId = evt.UserId, Balance = 0, UpdatedAt = DateTimeOffset.UtcNow, Version = 1 }; db.Wallets.Add(wallet);
        AddTransaction(wallet, 30, BalanceTransactionType.WelcomeBonus, null, "Welcome bonus", $"welcome:{evt.UserId}");
    }

    private async Task Reserve(MessageEnvelope envelope, CancellationToken ct)
    {
        var cmd = envelope.DeserializePayload<ReservePoints>();
        var existing = await db.Reservations.SingleOrDefaultAsync(x => x.SagaId == cmd.SagaId, ct);
        if (existing is not null)
        {
            if (existing.Status == ReservationStatus.Active) Outbox.AddEvent(db, new PointsReserved(cmd.SagaId, existing.Id, existing.UserId, existing.Amount), envelope.CorrelationId, envelope.MessageId);
            return;
        }
        var wallet = await db.Wallets.FromSqlInterpolated($"SELECT * FROM wallets WHERE \"UserId\" = {cmd.UserId} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (wallet is null) { Outbox.AddEvent(db, new PointsReservationFailed(cmd.SagaId, cmd.UserId, cmd.Amount, "Wallet not found."), envelope.CorrelationId, envelope.MessageId); return; }
        var reserved = await db.Reservations.Where(x => x.UserId == cmd.UserId && x.Status == ReservationStatus.Active).SumAsync(x => x.Amount, ct);
        if (wallet.Balance - reserved < cmd.Amount)
        {
            Outbox.AddEvent(db, new PointsReservationFailed(cmd.SagaId, cmd.UserId, cmd.Amount, "Insufficient available BD."), envelope.CorrelationId, envelope.MessageId); return;
        }
        var reservation = new PointsReservation { Id = Guid.NewGuid(), SagaId = cmd.SagaId, UserId = cmd.UserId, Amount = cmd.Amount, Status = ReservationStatus.Active, ExpiresAt = cmd.ExpiresAt, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.Reservations.Add(reservation); Outbox.AddEvent(db, new PointsReserved(cmd.SagaId, reservation.Id, cmd.UserId, cmd.Amount), envelope.CorrelationId, envelope.MessageId);
    }

    private async Task Release(MessageEnvelope envelope, CancellationToken ct)
    {
        var cmd = envelope.DeserializePayload<ReleasePoints>(); var reservation = await db.Reservations.SingleOrDefaultAsync(x => x.Id == cmd.ReservationId && x.SagaId == cmd.SagaId, ct);
        if (reservation is null || reservation.Status != ReservationStatus.Active) return; reservation.Status = ReservationStatus.Released; reservation.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private async Task Capture(MessageEnvelope envelope, CancellationToken ct)
    {
        var evt = envelope.DeserializePayload<LoanActivated>(); var reservation = await db.Reservations.SingleOrDefaultAsync(x => x.Id == evt.ReservationId && x.SagaId == evt.SagaId, ct);
        if (reservation is null || reservation.Status != ReservationStatus.Active) return;
        var wallet = await db.Wallets.FromSqlInterpolated($"SELECT * FROM wallets WHERE \"UserId\" = {evt.BorrowerId} FOR UPDATE").SingleAsync(ct);
        AddTransaction(wallet, -evt.Price, BalanceTransactionType.LoanPayment, evt.LoanId, "Loan payment captured", $"loan-payment:{evt.LoanId}"); reservation.Status = ReservationStatus.Captured; reservation.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private async Task Reward(MessageEnvelope envelope, CancellationToken ct)
    {
        var evt = envelope.DeserializePayload<LoanCompleted>(); var owner = await db.Wallets.SingleOrDefaultAsync(x => x.UserId == evt.OwnerId, ct);
        if (owner is not null && !await HasTransaction($"lender-reward:{evt.LoanId}", ct)) AddTransaction(owner, evt.LenderReward, BalanceTransactionType.SuccessfulLendingReward, evt.LoanId, "Successful lending reward", $"lender-reward:{evt.LoanId}");
        if (evt.ReturnedOnTime)
        {
            var borrower = await db.Wallets.SingleOrDefaultAsync(x => x.UserId == evt.BorrowerId, ct);
            if (borrower is not null && !await HasTransaction($"on-time:{evt.LoanId}", ct)) AddTransaction(borrower, 2, BalanceTransactionType.OnTimeReturnBonus, evt.LoanId, "On-time return bonus", $"on-time:{evt.LoanId}");
        }
    }

    private async Task Fine(MessageEnvelope envelope, CancellationToken ct)
    {
        var evt = envelope.DeserializePayload<LoanBecameOverdue>(); if (await db.Fines.AnyAsync(x => x.LoanId == evt.LoanId, ct)) return;
        var amount = Math.Min(20m, 5m + evt.DaysOverdue); var fine = new Fine { Id = Guid.NewGuid(), LoanId = evt.LoanId, UserId = evt.BorrowerId, Amount = amount, Reason = $"Loan overdue by {evt.DaysOverdue} day(s).", Status = FineStatus.Outstanding, CreatedAt = DateTimeOffset.UtcNow };
        db.Fines.Add(fine); var wallet = await db.Wallets.SingleOrDefaultAsync(x => x.UserId == evt.BorrowerId, ct);
        if (wallet is not null) AddTransaction(wallet, -amount, BalanceTransactionType.LateFine, evt.LoanId, fine.Reason, $"late-fine:{evt.LoanId}");
        Outbox.AddEvent(db, new FineCreated(fine.Id, fine.LoanId, fine.UserId, fine.Amount, fine.Reason), envelope.CorrelationId, envelope.MessageId);
    }

    private Task<bool> HasTransaction(string key, CancellationToken ct) => db.Transactions.AnyAsync(x => x.IdempotencyKey == key, ct);
    private void AddTransaction(Wallet wallet, decimal amount, BalanceTransactionType type, Guid? loanId, string description, string key)
    {
        wallet.Balance += amount; wallet.UpdatedAt = DateTimeOffset.UtcNow; wallet.Version++;
        db.Transactions.Add(new BalanceTransaction { Id = Guid.NewGuid(), UserId = wallet.UserId, Amount = amount, Type = type, LoanId = loanId, Description = description, CreatedAt = DateTimeOffset.UtcNow, IdempotencyKey = key });
    }
}
