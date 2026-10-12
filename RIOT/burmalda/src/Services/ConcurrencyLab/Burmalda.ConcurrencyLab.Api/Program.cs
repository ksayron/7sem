using Burmalda.ConcurrencyLab.Api;
using Burmalda.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddBurmaldaConsole(builder.Configuration);
builder.Services.AddSingleton<ExperimentStore>();
builder.Services.AddHealthChecks();
builder.Services.AddDbContextFactory<LabDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Database")));
builder.Services.AddSingleton<DatabaseLabStore>();

var app = builder.Build();
app.Lifetime.ApplicationStarted.Register(() => app.Logger.LogInformation("Laboratory ready · {Urls}", string.Join(", ", app.Urls)));
app.Use(async (context, next) =>
{
    try { await next(); }
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

if (app.Environment.IsDevelopment())
{
    app.MapConcurrencyLab();
    app.MapDatabaseLab();
}

await app.RunAsync();

public partial class Program;
