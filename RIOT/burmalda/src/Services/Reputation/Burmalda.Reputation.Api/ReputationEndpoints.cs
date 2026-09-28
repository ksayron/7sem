using Burmalda.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

namespace Burmalda.Reputation.Api;

public sealed record ReputationAdjustmentRequest(Guid UserId, int Delta, string Reason);

public static class ReputationEndpoints
{
    public static IEndpointRouteBuilder MapReputationEndpoints(this IEndpointRouteBuilder app)
    {
        var reputation = app.MapGroup("/api/reputation").RequireAuthorization();
        reputation.MapGet("/me", async (HttpContext context, ReputationDbContext db, CancellationToken ct) =>
        {
            var value = await db.Reputations.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == context.User.RequireUserId(), ct);
            return value is null ? Results.NotFound() : Results.Ok(value);
        });
        reputation.MapGet("/users/{id:guid}", async (Guid id, ReputationDbContext db, CancellationToken ct) =>
        {
            var value = await db.Reputations.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == id, ct);
            return value is null ? Results.NotFound() : Results.Ok(value);
        });

        var admin = app.MapGroup("/api/admin/reputation").RequireAuthorization("ModeratorOrAdmin");
        admin.MapGet("/users", async (ReputationDbContext db, int take, CancellationToken ct) => Results.Ok(await db.Reputations.AsNoTracking().OrderBy(x => x.Score).Take(Math.Clamp(take == 0 ? 100 : take, 1, 500)).ToListAsync(ct)));
        admin.MapPost("/adjustments", Adjust);
        admin.MapGet("/messaging", async (ReputationDbContext db, CancellationToken ct) => Results.Ok(new { PendingOutbox = await db.Set<OutboxMessage>().CountAsync(x => x.PublishedAt == null, ct), Inbox = await db.Set<InboxMessage>().CountAsync(ct) }));
        return app;
    }

    private static async Task<IResult> Adjust(ReputationAdjustmentRequest request, HttpContext context, ReputationDbContext db, CancellationToken ct)
    {
        if (request.Delta is < -100 or > 100 || string.IsNullOrWhiteSpace(request.Reason)) return Results.BadRequest();
        var reputation = await db.Reputations.SingleOrDefaultAsync(x => x.UserId == request.UserId, ct); if (reputation is null) return Results.NotFound();
        reputation.Score = Math.Clamp(reputation.Score + request.Delta, 0, 1000); reputation.UpdatedAt = DateTimeOffset.UtcNow; reputation.Version++;
        db.Adjustments.Add(new ReputationAdjustment { Id = Guid.NewGuid(), UserId = request.UserId, Delta = request.Delta, Reason = request.Reason, ActorId = context.User.RequireUserId(), CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct); return Results.Ok(reputation);
    }
}
