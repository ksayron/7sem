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

var app = builder.Build(); app.UseBurmaldaDefaults(); app.MapCatalogEndpoints();
app.MapGet("/api/catalog/runtime", (CatalogRuntime runtime) => runtime).RequireAuthorization();
var runtime = app.Services.GetRequiredService<CatalogRuntime>();
app.Logger.LogInformation("Catalog mode {Mode}; instance {Instance}", runtime.Mode, runtime.Instance);
await app.EnsureDatabaseAsync<CatalogDbContext>(); await app.RunAsync();

public partial class Program;
