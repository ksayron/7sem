using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Burmalda.ServiceDefaults;

public sealed class DatabaseReadiness<TDbContext>(TDbContext db) : IHealthCheck where TDbContext : DbContext
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Database unavailable.");
        }
        catch (Exception) { return HealthCheckResult.Unhealthy("Database unavailable."); }
    }
}

public sealed class RabbitReadiness(RabbitMqConnection rabbit) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var connection = await rabbit.GetAsync(cancellationToken);
            return connection.IsOpen ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("RabbitMQ unavailable.");
        }
        catch (Exception) { return HealthCheckResult.Unhealthy("RabbitMQ unavailable."); }
    }
}
