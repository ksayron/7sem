using Burmalda.Persistence;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Burmalda.LibraryGame;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Burmalda.LibraryGame.Server;

public static class LibraryHost
{
    public static IServiceCollection AddLibraryHost(this IServiceCollection services, IConfiguration configuration, bool jwt = false)
    {
        services.AddProblemDetails();
        services.AddExceptionHandler<DatabaseUnavailableHandler>();
        services.AddHealthChecks();
        services.AddHttpClient("library-internal", client => {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.Add("X-Library-Key", configuration["Library:InternalKey"] ?? "library-local-only-change-me");
        });
        services.AddHttpClient("library-read", client => {
            client.Timeout = TimeSpan.FromSeconds(2);
            client.DefaultRequestHeaders.Add("X-Library-Key", configuration["Library:InternalKey"] ?? "library-local-only-change-me");
        });
        if (jwt)
        {
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o => o.TokenValidationParameters = new() {
                ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
                ValidIssuer = configuration["Jwt:Issuer"] ?? "burmalda-identity", ValidAudience = configuration["Jwt:Audience"] ?? "burmalda-api",
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configuration["Jwt:SigningKey"] ?? "development-only-signing-key-change-before-use-32-chars")),
                NameClaimType = ClaimTypes.NameIdentifier, RoleClaimType = ClaimTypes.Role });
            services.AddAuthorization();
        }
        return services;
    }
    public static Guid UserId(this HttpContext context) => Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub")!);
    public static WebApplication UseLibraryHost(this WebApplication app, bool jwt = false)
    {
        app.UseExceptionHandler();
        if (jwt) { app.UseAuthentication(); app.UseAuthorization(); }
        var key = Encoding.UTF8.GetBytes(app.Configuration["Library:InternalKey"] ?? "library-local-only-change-me");
        if (!app.Environment.IsDevelopment() && app.Configuration["Library:InternalKey"] is null)
            throw new InvalidOperationException("Configure Library:InternalKey outside Development.");
        app.Use(async (context, next) => {
            if (context.Request.Path.StartsWithSegments("/internal") &&
                !CryptographicOperations.FixedTimeEquals(key, Encoding.UTF8.GetBytes(context.Request.Headers["X-Library-Key"].ToString())))
            { context.Response.StatusCode = 401; return; }
            try { await next(); }
            catch (GameRuleException ex) {
                context.Response.StatusCode = ex.Status; await context.Response.WriteAsJsonAsync(new { message = ex.Message });
            }
            catch (DbUpdateConcurrencyException) {
                context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { message = "Сохранение изменилось. Обновите состояние и повторите действие." });
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException or DbUpdateException or HttpRequestException or TaskCanceledException) {
                if (context.RequestAborted.IsCancellationRequested) return;
                app.Logger.LogWarning("Library request unavailable: {Kind}", ex.GetType().Name);
                context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { message = "Компонент временно недоступен. Повторите действие после восстановления связи." });
            }
        });
        app.MapHealthChecks("/health/ready");
        return app;
    }
    public static string Url(IConfiguration configuration, string service) => configuration[$"Library:{service}"] ?? service switch {
        "BrokerA" => "http://localhost:5109", "BrokerB" => "http://localhost:5110", "Subscribers" => "http://localhost:5111", _ => "http://localhost:5108" };
}

public sealed class OutgoingEvent
{
    public long Id { get; set; }
    public Guid EventId { get; set; }
    public string Json { get; set; } = "";
    public string Target { get; set; } = "BrokerA";
    public int Attempts { get; set; }
    public long NextAt { get; set; }
    public string? Error { get; set; }
}
public interface IOutboxDatabase { DbSet<OutgoingEvent> Outgoing { get; } }
public sealed class HttpPublisher(IHttpClientFactory clients, IConfiguration configuration, ILogger<HttpPublisher> logger)
{
    public async Task<BrokerReceipt> Publish(GameEvent message, string target, CancellationToken ct)
    {
        try {
            using var response = await clients.CreateClient("library-internal").PostAsJsonAsync($"{LibraryHost.Url(configuration, target)}/internal/events", message, ct);
            response.EnsureSuccessStatusCode();
            var receipt = await response.Content.ReadFromJsonAsync<BrokerReceipt>(ct) ?? throw new HttpRequestException("Empty broker receipt");
            logger.LogInformation("Publisher → {Broker}: {EventType} {EventId} accepted ({Recipients})", target, message.EventType, message.EventId, receipt.Recipients);
            return receipt;
        } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
            logger.LogWarning("Publisher → {Broker}: {EventId} FAILED {Error}", target, message.EventId, ex.Message); throw;
        }
    }
}
public sealed class OutboxWorker<T>(IDbContextFactory<T> factory, HttpPublisher publisher, ILogger<OutboxWorker<T>> logger) : BackgroundService where T : DbContext, IOutboxDatabase
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (!stoppingToken.IsCancellationRequested)
        {
            try {
                await using var db = await factory.CreateDbContextAsync(stoppingToken);
                // Do not overtake earlier publications if a broker is unavailable.
                var row = await db.Outgoing.OrderBy(x => x.Id).FirstOrDefaultAsync(stoppingToken);
                if (row is not null && row.NextAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) {
                    try { await publisher.Publish(GameJson.Read<GameEvent>(row.Json), row.Target, stoppingToken); db.Outgoing.Remove(row); }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !stoppingToken.IsCancellationRequested) {
                        row.Attempts++; row.Error = ex.Message; row.NextAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(15, row.Attempts)).ToUnixTimeMilliseconds();
                    }
                    await db.SaveChangesAsync(stoppingToken);
                }
            } catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogWarning("Outbox waiting for storage: {Kind}", ex.GetType().Name); }
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }
}
