using Burmalda.Persistence;
using Burmalda.Contracts;
using Burmalda.Catalog.Api;
using Burmalda.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddBurmaldaConsole(builder.Configuration);
builder.Services.AddBurmaldaDefaults(builder.Configuration, "catalog-service");
builder.Services.AddDbContext<CatalogDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Database")).WithDatabaseFailover());
builder.Services.AddServiceMessaging<CatalogDbContext, CatalogQueue>();
builder.Services.AddBusinessReadiness<CatalogDbContext>();
builder.Services.AddSingleton(CatalogRuntime.FromConfiguration(builder.Configuration));
foreach (var type in new[] { MessageNames.ReserveBookCopy, MessageNames.ReleaseBookCopy, MessageNames.LoanActivated, MessageNames.LoanCompleted })
    builder.Services.AddScoped<IIntegrationMessageHandler>(sp => new CatalogMessageHandler(sp.GetRequiredService<CatalogDbContext>(), type));

var app = builder.Build();
app.Use(async (context, next) => {
    context.Response.OnStarting(() => {
        context.Response.Headers["X-Burmalda-Catalog-Version"] = app.Configuration["BUILD_COMMIT"] ?? "local";
        context.Response.Headers["X-Burmalda-Catalog-Edition"] = ReleaseEdition.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Response.Headers["X-Burmalda-Catalog-Instance"] = Environment.MachineName;
        return Task.CompletedTask;
    });
    await next();
});
app.UseBurmaldaDefaults(); app.MapCatalogEndpoints();
app.MapGet("/api/catalog/runtime", (CatalogRuntime runtime) => runtime).RequireAuthorization();
var runtime = app.Services.GetRequiredService<CatalogRuntime>();
app.Logger.LogInformation("Catalog mode {Mode}; instance {Instance}", runtime.Mode, runtime.Instance);
await app.EnsureDatabaseAsync<CatalogDbContext>();
app.Logger.LogInformation("Catalog database initialized; ready to serve book exchange requests");
await app.RunAsync();

public partial class Program;
