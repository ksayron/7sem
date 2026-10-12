using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Burmalda.ServiceDefaults;

public static class Telemetry
{
    public const string SourceName = "Burmalda.Messaging";
    public static readonly ActivitySource ActivitySource = new(SourceName);
}

public static class ServiceDefaultsExtensions
{
    public static IServiceCollection AddBurmaldaDefaults(this IServiceCollection services, IConfiguration configuration, string serviceName)
    {
        services.AddProblemDetails();
        services.AddHealthChecks();
        services.Configure<RabbitMqOptions>(configuration.GetSection(RabbitMqOptions.SectionName));
        services.AddSingleton<RabbitMqConnection>();
        services.AddOpenTelemetry()
            .ConfigureResource(x => x.AddService(serviceName))
            .WithTracing(x => x
                .AddSource(Telemetry.SourceName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddEntityFrameworkCoreInstrumentation()
                .AddOtlpExporter())
            .WithMetrics(x => x
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter());
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = configuration["Jwt:Issuer"] ?? "burmalda-identity",
                    ValidAudience = configuration["Jwt:Audience"] ?? "burmalda-api",
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
                        configuration["Jwt:SigningKey"] ?? throw new InvalidOperationException("Jwt:SigningKey is required."))),
                    RoleClaimType = ClaimTypes.Role,
                    NameClaimType = ClaimTypes.NameIdentifier
                };
            });
        services.AddAuthorizationBuilder()
            .AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"))
            .AddPolicy("ModeratorOrAdmin", policy => policy.RequireRole("Moderator", "Admin"));
        return services;
    }

    public static IServiceCollection AddServiceMessaging<TDbContext, TQueueDefinition>(this IServiceCollection services)
        where TDbContext : DbContext
        where TQueueDefinition : class, IMessageQueueDefinition
    {
        services.AddSingleton<IMessageQueueDefinition, TQueueDefinition>();
        services.AddHostedService<OutboxPublisher<TDbContext>>();
        services.AddHostedService<MessageConsumer<TDbContext>>();
        return services;
    }

    public static WebApplication UseBurmaldaDefaults(this WebApplication app)
    {
        app.Lifetime.ApplicationStarted.Register(() => app.Logger.LogInformation("Ready · {Urls}", string.Join(", ", app.Urls)));
        app.UseExceptionHandler();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHealthChecks("/health/live").AllowAnonymous();
        app.MapHealthChecks("/health/ready").AllowAnonymous();
        return app;
    }

    public static async Task EnsureDatabaseAsync<TDbContext>(this WebApplication app)
        where TDbContext : DbContext
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var created = await db.Database.EnsureCreatedAsync();
        if (created || !db.Database.IsRelational()) return;

        // Hangfire may create its own tables before EF's local-development
        // bootstrap runs. EnsureCreated intentionally does nothing in a
        // non-empty database, so create this DbContext's tables when its
        // outbox sentinel is absent.
        var entityType = db.Model.FindEntityType(typeof(OutboxMessage));
        var tableName = entityType?.GetTableName();
        var schema = entityType?.GetSchema() ?? "public";
        if (string.IsNullOrWhiteSpace(tableName)) return;

        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT to_regclass(@qualified_name) IS NOT NULL";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "qualified_name";
            parameter.Value = $"{schema}.{tableName}";
            command.Parameters.Add(parameter);
            var exists = (bool)(await command.ExecuteScalarAsync() ?? false);
            if (!exists)
                await db.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}

public static class ClaimsPrincipalExtensions
{
    public static Guid RequireUserId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        return Guid.TryParse(value, out var id) ? id : throw new BadHttpRequestException("Authenticated user id is missing.");
    }
}
