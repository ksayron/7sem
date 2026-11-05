using Burmalda.Persistence;
using Burmalda.ConcurrencyLab.Api;
using Burmalda.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using Burmalda.PostgresControl;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddBurmaldaConsole(builder.Configuration);
builder.Services.AddSingleton<ExperimentStore>();
builder.Services.AddHealthChecks();
builder.Services.AddDbContextFactory<LabDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Database")).WithDatabaseFailover());
builder.Services.AddSingleton<DatabaseLabStore>();
builder.Services.AddSingleton<RaftControlClient>();
builder.Services.AddSingleton<PostgresReplicationCluster>();
builder.Services.AddSingleton<IReplicationCluster, RaftManagedReplicationCluster>();
builder.Services.AddSingleton(sp => new ReplicationLabStore(
    builder.Configuration["ReplicationLab:JournalRoot"] ?? Path.Combine(PostgresReplicationCluster.ResolveRoot(builder.Configuration["ReplicationLab:ComposeRoot"]), ".artifacts", "streaming-replication-raft"),
    sp.GetRequiredService<IReplicationCluster>(), sp.GetRequiredService<ILogger<ReplicationLabStore>>(),
    sp.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));
// PostgreSQL failover is owned by the five Raft nodes, never by this facade.

var app = builder.Build();
app.Lifetime.ApplicationStarted.Register(() => app.Logger.LogInformation("Laboratory ready · {Urls}", string.Join(", ", app.Urls)));
app.Use(async (context, next) =>
{
    try { await next(); }
    catch (BadHttpRequestException) when (context.Request.Path.StartsWithSegments("/api/concurrency-lab/replication"))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { message = "Некорректный запрос. Проверьте JSON, ClientId, CopyId, Available и ExpectedVersion." });
    }
    catch (ReplicationLabException ex)
    {
        app.Logger.LogWarning("LR5 HTTP {Status}: {Message}", ex.Status, ex.Message);
        context.Response.StatusCode = ex.Status;
        await context.Response.WriteAsJsonAsync(new { message = ex.Message });
    }
    catch (Exception ex) when (context.Request.Path.StartsWithSegments("/api/concurrency-lab/replication") &&
        ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
    {
        app.Logger.LogWarning("LR5 storage error: {Kind}", ex.GetType().Name);
        context.Response.StatusCode = 503;
        await context.Response.WriteAsJsonAsync(new { message = "Журнал координатора недоступен. Проверьте права доступа и состояние кластера." });
    }
    catch (Exception ex) when (ex is LabNotFoundException or LabConflictException or DbUpdateException or System.Data.Common.DbException or TimeoutException)
    {
        var constraint = ex is Npgsql.PostgresException { SqlState: var code } && code.StartsWith("23", StringComparison.Ordinal) ||
            ex is DbUpdateException { InnerException: Npgsql.PostgresException { SqlState: var innerCode } } && innerCode.StartsWith("23", StringComparison.Ordinal);
        var status = ex is LabNotFoundException ? 404 : ex is LabConflictException || constraint ? 409 : 503;
        app.Logger.Log(status == 503 ? LogLevel.Error : LogLevel.Warning, "Lab HTTP {Status}: {Kind}", status, ex.GetType().Name);
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { message = ex is LabConflictException ? ex.Message : status == 404 ? "Опыт или ресурс не найден." : constraint ? "Нарушение ограничения целостности БД. Проверьте данные операции." : "База данных недоступна или операция БД не выполнена. Проверьте PostgreSQL и подключение." });
    }
});
app.MapHealthChecks("/health/ready");
if (app.Environment.IsDevelopment()) app.MapRaftLab();

if (app.Environment.IsDevelopment())
{
    app.MapConcurrencyLab();
    app.MapDatabaseLab();
    app.MapReplicationLab();
}

await app.RunAsync();

public partial class Program;
