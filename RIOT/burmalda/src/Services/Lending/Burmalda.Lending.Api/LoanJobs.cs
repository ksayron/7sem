using Burmalda.Contracts;
using Burmalda.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

namespace Burmalda.Lending.Api;

public sealed class LoanJobs(IServiceScopeFactory scopeFactory)
{
    public async Task DetectOverdue()
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LendingDbContext>();
        var now = DateTimeOffset.UtcNow;
        var loans = await db.Loans.Where(x => x.Status == LoanStatus.Active && x.DueAt < now).ToListAsync();
        foreach (var loan in loans)
        {
            loan.MarkOverdue(now);
            LoanHistory.Add(db, loan, null, "Scheduled overdue detection.");
            Outbox.AddEvent(db, new LoanBecameOverdue(loan.Id, loan.CopyId, loan.BorrowerId, loan.OwnerId, Math.Max(1, (now - loan.DueAt!.Value).Days)), loan.SagaId);
        }
        await db.SaveChangesAsync();
    }

    public async Task ExpireWorkflows()
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LendingDbContext>();
        var now = DateTimeOffset.UtcNow;
        var sagas = await db.Sagas.Where(x => x.Deadline < now && (x.State == LoanSagaState.AwaitingCopyReservation || x.State == LoanSagaState.AwaitingPointsReservation)).ToListAsync();
        foreach (var saga in sagas)
            await TimeoutSaga(db, saga, CancellationToken.None);
        var handovers = await db.Loans.Where(x => x.Status == LoanStatus.Accepted && x.HandoverExpiresAt < now).ToListAsync();
        foreach (var loan in handovers)
        {
            loan.Status = LoanStatus.Cancelled;
            loan.Version++;
            loan.UpdatedAt = now;
            LoanHistory.Add(db, loan, null, "Handover window expired.");
            Outbox.AddCommand(db, "catalog", new ReleaseBookCopy(loan.SagaId, loan.CopyId, "Handover expired."), loan.SagaId);
            Outbox.AddCommand(db, "economy", new ReleasePoints(loan.SagaId, loan.ReservationId, "Handover expired."), loan.SagaId);
            Outbox.AddEvent(db, new LoanCancelled(loan.Id, loan.SagaId, loan.CopyId, loan.BorrowerId, loan.OwnerId, "Handover expired."), loan.SagaId);
        }
        await db.SaveChangesAsync();
    }

    public async Task SendReturnReminders()
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LendingDbContext>();
        var now = DateTimeOffset.UtcNow;
        var loans = await db.Loans.Where(x => x.Status == LoanStatus.Active && x.DueAt <= now.AddDays(3)).ToListAsync();
        foreach (var loan in loans)
        {
            var days = Math.Max(0, (int)Math.Ceiling((loan.DueAt!.Value - now).TotalDays));
            var bit = days switch { 3 => 1, 1 => 2, 0 => 4, _ => 0 };
            if (bit == 0 || (loan.ReminderFlags & bit) != 0)
                continue;
            loan.ReminderFlags |= bit;
            Outbox.AddEvent(db, new ReturnReminderDue(loan.Id, loan.BorrowerId, loan.DueAt.Value, days), loan.SagaId);
        }
        await db.SaveChangesAsync();
    }

    public static Task TimeoutSaga(LendingDbContext db, LoanRequestSaga saga, CancellationToken ct)
    {
        if (saga.State == LoanSagaState.AwaitingPointsReservation)
            Outbox.AddCommand(db, "catalog", new ReleaseBookCopy(saga.Id, saga.CopyId, "Saga timed out."), saga.Id);
        if (saga.PointsReservationId is { } reservationId)
            Outbox.AddCommand(db, "economy", new ReleasePoints(saga.Id, reservationId, "Saga timed out."), saga.Id);
        saga.State = LoanSagaState.TimedOut;
        saga.FailureReason = "Workflow timed out.";
        saga.UpdatedAt = DateTimeOffset.UtcNow;
        return Task.CompletedTask;
    }
}
