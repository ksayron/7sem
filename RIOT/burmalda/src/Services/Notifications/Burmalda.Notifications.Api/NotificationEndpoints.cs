using System.Text.Json;
using Burmalda.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

namespace Burmalda.Notifications.Api;

public sealed record BroadcastRequest(string Title, string Body, Guid[] UserIds);

public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var notifications = app.MapGroup("/api/notifications").RequireAuthorization();
        notifications.MapGet("/", GetMine); notifications.MapGet("/unread-count", UnreadCount); notifications.MapPut("/{id:guid}/read", MarkRead); notifications.MapPut("/read-all", ReadAll);
        var admin = app.MapGroup("/api/admin/notifications").RequireAuthorization("AdminOnly");
        admin.MapPost("/broadcast", Broadcast); admin.MapGet("/recent", Recent);
        admin.MapGet("/messaging", async (NotificationDbContext db, CancellationToken ct) => Results.Ok(new { PendingOutbox = await db.Set<OutboxMessage>().CountAsync(x => x.PublishedAt == null, ct), Inbox = await db.Set<InboxMessage>().CountAsync(ct) }));
        return app;
    }
    private static async Task<IResult> GetMine(HttpContext context, NotificationDbContext db, int skip = 0, int take = 100, CancellationToken ct = default) => Results.Ok(await db.Notifications.AsNoTracking().Where(x => x.UserId == context.User.RequireUserId()).OrderByDescending(x => x.CreatedAt).Skip(skip).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct));
    private static async Task<IResult> UnreadCount(HttpContext context, NotificationDbContext db, CancellationToken ct) => Results.Ok(new { Count = await db.Notifications.CountAsync(x => x.UserId == context.User.RequireUserId() && x.ReadAt == null, ct) });
    private static async Task<IResult> MarkRead(Guid id, HttpContext context, NotificationDbContext db, CancellationToken ct) { var user = context.User.RequireUserId(); var value = await db.Notifications.SingleOrDefaultAsync(x => x.Id == id && x.UserId == user, ct); if (value is null) return Results.NotFound(); value.ReadAt ??= DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct); return Results.NoContent(); }
    private static async Task<IResult> ReadAll(HttpContext context, NotificationDbContext db, CancellationToken ct) { var user = context.User.RequireUserId(); await db.Notifications.Where(x => x.UserId == user && x.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.ReadAt, DateTimeOffset.UtcNow), ct); return Results.NoContent(); }
    private static async Task<IResult> Broadcast(BroadcastRequest request, NotificationDbContext db, CancellationToken ct) { if (request.UserIds.Length == 0) return Results.BadRequest(new { message = "At least one recipient is required." }); foreach (var id in request.UserIds.Distinct()) db.Notifications.Add(New(id, "AdminBroadcast", request.Title, request.Body, new { })); await db.SaveChangesAsync(ct); return Results.Accepted(); }
    private static async Task<IResult> Recent(NotificationDbContext db, int take = 100, CancellationToken ct = default) => Results.Ok(await db.Notifications.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct));
    internal static Notification New(Guid userId, string type, string title, string body, object data) => new() { Id = Guid.NewGuid(), UserId = userId, Type = type, Title = title, Body = body, DataJson = JsonSerializer.Serialize(data), CreatedAt = DateTimeOffset.UtcNow };
}
