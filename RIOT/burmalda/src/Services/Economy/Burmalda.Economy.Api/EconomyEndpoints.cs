using Burmalda.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

namespace Burmalda.Economy.Api;

public sealed record AdjustmentRequest(Guid UserId, decimal Amount, string Description);

public static class EconomyEndpoints
{
    public static IEndpointRouteBuilder MapEconomyEndpoints(this IEndpointRouteBuilder app)
    {
        var economy = app.MapGroup("/api/economy").RequireAuthorization();
        economy.MapGet("/wallet", GetWallet);
        economy.MapGet("/transactions", GetTransactions);
        economy.MapGet("/fines", GetFines);
        var admin = app.MapGroup("/api/admin/economy").RequireAuthorization("AdminOnly");
        admin.MapGet("/wallets", GetWallets);
        admin.MapGet("/reservations", GetReservations);
        admin.MapGet("/fines", GetAllFines);
        admin.MapPost("/adjustments", Adjust);
        admin.MapGet("/messaging", MessagingStatus);
        return app;
    }

    private static async Task<IResult> GetWallet(HttpContext context, EconomyDbContext db, CancellationToken ct)
    {
        var user = context.User.RequireUserId(); var wallet = await db.Wallets.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == user, ct); if (wallet is null) return Results.NotFound();
        var reserved = await db.Reservations.Where(x => x.UserId == user && x.Status == ReservationStatus.Active).SumAsync(x => x.Amount, ct);
        return Results.Ok(new { wallet.UserId, wallet.Balance, Reserved = reserved, Available = wallet.Balance - reserved, wallet.UpdatedAt });
    }
    private static async Task<IResult> GetTransactions(HttpContext context, EconomyDbContext db, int skip = 0, int take = 100, CancellationToken ct = default) => Results.Ok(await db.Transactions.AsNoTracking().Where(x => x.UserId == context.User.RequireUserId()).OrderByDescending(x => x.CreatedAt).Skip(skip).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct));
    private static async Task<IResult> GetFines(HttpContext context, EconomyDbContext db, CancellationToken ct) => Results.Ok(await db.Fines.AsNoTracking().Where(x => x.UserId == context.User.RequireUserId()).OrderByDescending(x => x.CreatedAt).ToListAsync(ct));
    private static async Task<IResult> GetWallets(EconomyDbContext db, int take = 200, CancellationToken ct = default) => Results.Ok(await db.Wallets.AsNoTracking().OrderByDescending(x => x.Balance).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct));
    private static async Task<IResult> GetReservations(EconomyDbContext db, ReservationStatus? status, CancellationToken ct) => Results.Ok(await db.Reservations.AsNoTracking().Where(x => status == null || x.Status == status).OrderByDescending(x => x.CreatedAt).Take(500).ToListAsync(ct));
    private static async Task<IResult> GetAllFines(EconomyDbContext db, FineStatus? status, CancellationToken ct) => Results.Ok(await db.Fines.AsNoTracking().Where(x => status == null || x.Status == status).OrderByDescending(x => x.CreatedAt).Take(500).ToListAsync(ct));
    private static async Task<IResult> Adjust(AdjustmentRequest request, HttpContext context, EconomyDbContext db, CancellationToken ct)
    {
        if (request.Amount == 0 || string.IsNullOrWhiteSpace(request.Description)) return Results.BadRequest();
        var wallet = await db.Wallets.SingleOrDefaultAsync(x => x.UserId == request.UserId, ct); if (wallet is null) return Results.NotFound();
        if (wallet.Balance + request.Amount < 0) return Results.Conflict(new { message = "Adjustment would make balance negative." });
        wallet.Balance += request.Amount; wallet.UpdatedAt = DateTimeOffset.UtcNow; wallet.Version++;
        db.Transactions.Add(new BalanceTransaction { Id = Guid.NewGuid(), UserId = request.UserId, Amount = request.Amount, Type = BalanceTransactionType.AdministrativeAdjustment, Description = request.Description, CreatedAt = DateTimeOffset.UtcNow, IdempotencyKey = $"admin:{Guid.NewGuid()}:{context.User.RequireUserId()}" });
        await db.SaveChangesAsync(ct); return Results.Ok(new { wallet.UserId, wallet.Balance });
    }
    private static async Task<IResult> MessagingStatus(EconomyDbContext db, CancellationToken ct) => Results.Ok(new { PendingOutbox = await db.Set<OutboxMessage>().CountAsync(x => x.PublishedAt == null, ct), FailedOutbox = await db.Set<OutboxMessage>().CountAsync(x => x.PublishedAt == null && x.RetryCount > 0, ct), Inbox = await db.Set<InboxMessage>().CountAsync(ct) });
}
