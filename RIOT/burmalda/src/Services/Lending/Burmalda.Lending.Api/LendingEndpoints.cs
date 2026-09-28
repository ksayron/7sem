using Burmalda.Contracts;
using Burmalda.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

namespace Burmalda.Lending.Api;

public sealed record RequestLoanRequest(Guid CopyId);
public sealed record RejectLoanRequest(string? Reason);
public sealed record ConfirmHandoverRequest(string Code);
public sealed record ResolveDisputeRequest(string Resolution, bool MarkCompleted);

public static class LendingEndpoints
{
    public static IEndpointRouteBuilder MapLendingEndpoints(this IEndpointRouteBuilder app)
    {
        var loans = app.MapGroup("/api/loans").RequireAuthorization();
        loans.MapPost("/requests", RequestLoan);
        loans.MapGet("/requests/{sagaId:guid}", GetSaga);
        loans.MapGet("/mine", GetMine);
        loans.MapGet("/{id:guid}", GetLoan);
        loans.MapPost("/{id:guid}/accept", Accept);
        loans.MapPost("/{id:guid}/reject", Reject);
        loans.MapPost("/{id:guid}/cancel", Cancel);
        loans.MapPost("/{id:guid}/handover/confirm", ConfirmHandover);
        loans.MapPost("/{id:guid}/return", InitiateReturn);
        loans.MapPost("/{id:guid}/return/confirm", ConfirmReturn);

        var admin = app.MapGroup("/api/admin/lending").RequireAuthorization("ModeratorOrAdmin");
        admin.MapGet("/loans", AdminLoans);
        admin.MapGet("/sagas", AdminSagas);
        admin.MapPost("/sagas/{id:guid}/timeout", TimeoutSaga);
        admin.MapPost("/loans/{id:guid}/resolve-dispute", ResolveDispute);
        admin.MapGet("/messaging", MessagingStatus);
        return app;
    }

    private static async Task<IResult> RequestLoan(RequestLoanRequest request, HttpContext context, LendingDbContext db, CancellationToken ct)
    {
        var userId = context.User.RequireUserId(); var now = DateTimeOffset.UtcNow; var sagaId = Guid.NewGuid();
        var saga = new LoanRequestSaga { Id = sagaId, CopyId = request.CopyId, BorrowerId = userId, State = LoanSagaState.AwaitingCopyReservation, Deadline = now.AddMinutes(2), CreatedAt = now, UpdatedAt = now };
        db.Sagas.Add(saga);
        Outbox.AddCommand(db, "catalog", new ReserveBookCopy(saga.Id, saga.CopyId, userId, saga.Deadline), saga.Id);
        await db.SaveChangesAsync(ct);
        return Results.Accepted($"/api/loans/requests/{saga.Id}", new { saga.Id, saga.State, saga.Deadline });
    }

    private static async Task<IResult> GetSaga(Guid sagaId, HttpContext context, LendingDbContext db, CancellationToken ct)
    {
        var user = context.User.RequireUserId(); var saga = await db.Sagas.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sagaId && x.BorrowerId == user, ct);
        return saga is null ? Results.NotFound() : Results.Ok(saga);
    }

    private static async Task<IResult> GetMine(HttpContext context, LendingDbContext db, CancellationToken ct)
    {
        var user = context.User.RequireUserId(); return Results.Ok(await db.Loans.AsNoTracking().Where(x => x.BorrowerId == user || x.OwnerId == user).OrderByDescending(x => x.CreatedAt).ToListAsync(ct));
    }

    private static async Task<IResult> GetLoan(Guid id, HttpContext context, LendingDbContext db, CancellationToken ct)
    {
        var user = context.User.RequireUserId(); var loan = await db.Loans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && (x.BorrowerId == user || x.OwnerId == user), ct);
        return loan is null ? Results.NotFound() : Results.Ok(new { Loan = loan, History = await db.StatusHistory.AsNoTracking().Where(x => x.LoanId == id).OrderBy(x => x.OccurredAt).ToListAsync(ct) });
    }

    private static Task<IResult> Accept(Guid id, HttpContext context, LendingDbContext db, CancellationToken ct) => Transition(id, context, db, ct, (loan, actor) =>
    {
        var code = loan.Accept(actor, DateTimeOffset.UtcNow); LoanHistory.Add(db, loan, actor, "Owner accepted request.");
        Outbox.AddEvent(db, new LoanAccepted(loan.Id, loan.CopyId, loan.BorrowerId, loan.OwnerId, loan.HandoverExpiresAt!.Value), loan.SagaId);
        return Results.Ok(new { loan.Id, loan.Status, HandoverCode = code, loan.HandoverExpiresAt });
    });

    private static Task<IResult> Reject(Guid id, RejectLoanRequest request, HttpContext context, LendingDbContext db, CancellationToken ct) => Transition(id, context, db, ct, (loan, actor) =>
    {
        loan.Reject(actor, DateTimeOffset.UtcNow); var reason = request.Reason ?? "Owner rejected request."; LoanHistory.Add(db, loan, actor, reason); Compensate(db, loan, reason);
        Outbox.AddEvent(db, new LoanRejected(loan.Id, loan.SagaId, loan.CopyId, loan.BorrowerId, loan.OwnerId, reason), loan.SagaId); return Results.NoContent();
    });

    private static Task<IResult> Cancel(Guid id, HttpContext context, LendingDbContext db, CancellationToken ct) => Transition(id, context, db, ct, (loan, actor) =>
    {
        loan.Cancel(actor, DateTimeOffset.UtcNow); const string reason = "Borrower cancelled request."; LoanHistory.Add(db, loan, actor, reason); Compensate(db, loan, reason);
        Outbox.AddEvent(db, new LoanCancelled(loan.Id, loan.SagaId, loan.CopyId, loan.BorrowerId, loan.OwnerId, reason), loan.SagaId); return Results.NoContent();
    });

    private static Task<IResult> ConfirmHandover(Guid id, ConfirmHandoverRequest request, HttpContext context, LendingDbContext db, CancellationToken ct) => Transition(id, context, db, ct, (loan, actor) =>
    {
        loan.ConfirmHandover(actor, request.Code, DateTimeOffset.UtcNow); LoanHistory.Add(db, loan, actor, "Borrower confirmed physical handover.");
        Outbox.AddEvent(db, new LoanActivated(loan.Id, loan.SagaId, loan.ReservationId, loan.CopyId, loan.BorrowerId, loan.OwnerId, loan.Price, loan.DueAt!.Value), loan.SagaId); return Results.Ok(new { loan.Id, loan.Status, loan.DueAt });
    });

    private static Task<IResult> InitiateReturn(Guid id, HttpContext context, LendingDbContext db, CancellationToken ct) => Transition(id, context, db, ct, (loan, actor) =>
    {
        loan.InitiateReturn(actor, DateTimeOffset.UtcNow); LoanHistory.Add(db, loan, actor, "Borrower initiated return.");
        Outbox.AddEvent(db, new ReturnInitiated(loan.Id, loan.CopyId, loan.BorrowerId, loan.OwnerId), loan.SagaId); return Results.Ok(new { loan.Id, loan.Status });
    });

    private static Task<IResult> ConfirmReturn(Guid id, HttpContext context, LendingDbContext db, CancellationToken ct) => Transition(id, context, db, ct, (loan, actor) =>
    {
        var onTime = loan.CompleteReturn(actor, DateTimeOffset.UtcNow); LoanHistory.Add(db, loan, actor, "Owner confirmed return.");
        Outbox.AddEvent(db, new LoanCompleted(loan.Id, loan.CopyId, loan.BorrowerId, loan.OwnerId, loan.Price, onTime), loan.SagaId); return Results.Ok(new { loan.Id, loan.Status, ReturnedOnTime = onTime });
    });

    private static async Task<IResult> Transition(Guid id, HttpContext context, LendingDbContext db, CancellationToken ct, Func<Loan, Guid, IResult> action)
    {
        var loan = await db.Loans.SingleOrDefaultAsync(x => x.Id == id, ct); if (loan is null) return Results.NotFound();
        try { var result = action(loan, context.User.RequireUserId()); await db.SaveChangesAsync(ct); return result; }
        catch (DomainRuleException ex) { return Results.Conflict(new { message = ex.Message, loan.Status }); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { message = "Loan changed concurrently. Refresh and retry." }); }
    }

    private static void Compensate(LendingDbContext db, Loan loan, string reason)
    {
        Outbox.AddCommand(db, "catalog", new ReleaseBookCopy(loan.SagaId, loan.CopyId, reason), loan.SagaId);
        Outbox.AddCommand(db, "economy", new ReleasePoints(loan.SagaId, loan.ReservationId, reason), loan.SagaId);
    }

    private static async Task<IResult> AdminLoans(LendingDbContext db, LoanStatus? status, int take = 200, CancellationToken ct = default) => Results.Ok(await db.Loans.AsNoTracking().Where(x => status == null || x.Status == status).OrderByDescending(x => x.CreatedAt).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct));
    private static async Task<IResult> AdminSagas(LendingDbContext db, LoanSagaState? state, int take = 200, CancellationToken ct = default) => Results.Ok(await db.Sagas.AsNoTracking().Where(x => state == null || x.State == state).OrderByDescending(x => x.CreatedAt).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct));

    private static async Task<IResult> TimeoutSaga(Guid id, LendingDbContext db, CancellationToken ct)
    {
        var saga = await db.Sagas.SingleOrDefaultAsync(x => x.Id == id, ct); if (saga is null) return Results.NotFound();
        await LoanJobs.TimeoutSaga(db, saga, ct); await db.SaveChangesAsync(ct); return Results.NoContent();
    }

    private static async Task<IResult> ResolveDispute(Guid id, ResolveDisputeRequest request, LendingDbContext db, CancellationToken ct)
    {
        var loan = await db.Loans.SingleOrDefaultAsync(x => x.Id == id && x.Status == LoanStatus.Disputed, ct); if (loan is null) return Results.NotFound();
        loan.Status = request.MarkCompleted ? LoanStatus.Completed : LoanStatus.Active; loan.Version++; loan.UpdatedAt = DateTimeOffset.UtcNow; LoanHistory.Add(db, loan, null, $"Admin resolution: {request.Resolution}");
        if (request.MarkCompleted) Outbox.AddEvent(db, new LoanCompleted(loan.Id, loan.CopyId, loan.BorrowerId, loan.OwnerId, loan.Price, false), loan.SagaId);
        await db.SaveChangesAsync(ct); return Results.NoContent();
    }

    private static async Task<IResult> MessagingStatus(LendingDbContext db, CancellationToken ct) => Results.Ok(new { PendingOutbox = await db.Set<OutboxMessage>().CountAsync(x => x.PublishedAt == null, ct), Inbox = await db.Set<InboxMessage>().CountAsync(ct) });
}
