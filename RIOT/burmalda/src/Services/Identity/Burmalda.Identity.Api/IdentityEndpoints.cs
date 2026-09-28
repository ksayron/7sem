using Burmalda.Contracts;
using Burmalda.ServiceDefaults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Burmalda.Identity.Api;

public sealed record RegisterRequest(string Email, string DisplayName, string Password);
public sealed record LoginRequest(string Email, string Password);
public sealed record RefreshRequest(string RefreshToken);
public sealed record ChangeRoleRequest(string Role);

public static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/identity");
        auth.MapPost("/register", Register);
        auth.MapPost("/login", Login);
        auth.MapPost("/refresh", Refresh);
        auth.MapPost("/logout", Logout).RequireAuthorization();
        auth.MapGet("/me", Me).RequireAuthorization();

        var admin = app.MapGroup("/api/admin/identity").RequireAuthorization("AdminOnly");
        admin.MapGet("/users", GetUsers);
        admin.MapPut("/users/{id:guid}/role", ChangeRole);
        admin.MapPut("/users/{id:guid}/active/{active:bool}", SetActive);
        return app;
    }

    private static async Task<IResult> Register(RegisterRequest request, IdentityDbContext db, IPasswordHasher<AppUser> hasher, CancellationToken ct)
    {
        var normalized = request.Email.Trim().ToUpperInvariant();
        if (request.Password.Length < 8 || string.IsNullOrWhiteSpace(request.DisplayName))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = ["Display name is required and password must be at least 8 characters."] });
        if (await db.Users.AnyAsync(x => x.NormalizedEmail == normalized, ct))
            return Results.Conflict(new { message = "An account with this email already exists." });
        var user = new AppUser
        {
            Id = Guid.NewGuid(), Email = request.Email.Trim(), NormalizedEmail = normalized,
            DisplayName = request.DisplayName.Trim(), PasswordHash = string.Empty, Role = "User", CreatedAt = DateTimeOffset.UtcNow
        };
        user.PasswordHash = hasher.HashPassword(user, request.Password);
        db.Users.Add(user);
        Outbox.AddEvent(db, new UserRegistered(user.Id, user.Email, user.DisplayName), user.Id);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/admin/identity/users/{user.Id}", new { user.Id, user.Email, user.DisplayName });
    }

    private static async Task<IResult> Login(LoginRequest request, IdentityDbContext db, IPasswordHasher<AppUser> hasher, TokenService tokens, CancellationToken ct)
    {
        var normalized = request.Email.Trim().ToUpperInvariant();
        var user = await db.Users.SingleOrDefaultAsync(x => x.NormalizedEmail == normalized, ct);
        if (user is null || !user.IsActive || hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
            return Results.Unauthorized();
        return await IssueTokens(user, db, tokens, ct);
    }

    private static async Task<IResult> Refresh(RefreshRequest request, IdentityDbContext db, TokenService tokens, CancellationToken ct)
    {
        var hash = TokenService.HashToken(request.RefreshToken);
        var refresh = await db.RefreshTokens.SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
        if (refresh is null || refresh.RevokedAt is not null || refresh.ExpiresAt <= DateTimeOffset.UtcNow) return Results.Unauthorized();
        var user = await db.Users.SingleAsync(x => x.Id == refresh.UserId, ct);
        if (!user.IsActive) return Results.Unauthorized();
        refresh.RevokedAt = DateTimeOffset.UtcNow;
        return await IssueTokens(user, db, tokens, ct);
    }

    private static async Task<IResult> Logout(RefreshRequest request, IdentityDbContext db, CancellationToken ct)
    {
        var hash = TokenService.HashToken(request.RefreshToken);
        var token = await db.RefreshTokens.SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
        if (token is not null) { token.RevokedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct); }
        return Results.NoContent();
    }

    private static async Task<IResult> IssueTokens(AppUser user, IdentityDbContext db, TokenService tokens, CancellationToken ct)
    {
        var access = tokens.CreateAccessToken(user);
        var refreshToken = TokenService.CreateRefreshToken();
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(), UserId = user.Id, TokenHash = TokenService.HashToken(refreshToken),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        });
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { access.AccessToken, access.ExpiresAt, RefreshToken = refreshToken, user.Id, user.DisplayName, user.Role });
    }

    private static IResult Me(HttpContext context) => Results.Ok(new
    {
        Id = context.User.RequireUserId(),
        Name = context.User.Identity?.Name,
        Role = context.User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value
    });

    private static async Task<IResult> GetUsers(IdentityDbContext db, int skip = 0, int take = 100, CancellationToken ct = default) =>
        Results.Ok(await db.Users.AsNoTracking().OrderByDescending(x => x.CreatedAt).Skip(skip).Take(Math.Clamp(take, 1, 200))
            .Select(x => new { x.Id, x.Email, x.DisplayName, x.Role, x.IsActive, x.CreatedAt }).ToListAsync(ct));

    private static async Task<IResult> ChangeRole(Guid id, ChangeRoleRequest request, IdentityDbContext db, CancellationToken ct)
    {
        if (request.Role is not ("User" or "Moderator" or "Admin")) return Results.BadRequest(new { message = "Unknown role." });
        var user = await db.Users.FindAsync([id], ct);
        if (user is null) return Results.NotFound();
        user.Role = request.Role;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SetActive(Guid id, bool active, IdentityDbContext db, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([id], ct);
        if (user is null) return Results.NotFound();
        user.IsActive = active;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
