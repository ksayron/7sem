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
foreach (var type in new[] { MessageNames.ReserveBookCopy, MessageNames.ReleaseBookCopy, MessageNames.LoanActivated, MessageNames.LoanCompleted })
    builder.Services.AddScoped<IIntegrationMessageHandler>(sp => new CatalogMessageHandler(sp.GetRequiredService<CatalogDbContext>(), type));

var app = builder.Build(); app.UseBurmaldaDefaults(); app.MapCatalogEndpoints();
await app.EnsureDatabaseAsync<CatalogDbContext>(); await app.RunAsync();

public partial class Program;
