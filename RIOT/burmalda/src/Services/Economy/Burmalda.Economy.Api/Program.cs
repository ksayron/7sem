using Burmalda.Persistence;
using Burmalda.Contracts;
using Burmalda.Economy.Api;
using Burmalda.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddBurmaldaConsole(builder.Configuration); builder.Services.AddBurmaldaDefaults(builder.Configuration, "economy-service");
builder.Services.AddDbContext<EconomyDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Database")).WithDatabaseFailover());
builder.Services.AddServiceMessaging<EconomyDbContext, EconomyQueue>();
foreach (var type in new[] { MessageNames.UserRegistered, MessageNames.ReservePoints, MessageNames.ReleasePoints, MessageNames.LoanActivated, MessageNames.LoanCompleted, MessageNames.LoanBecameOverdue })
    builder.Services.AddScoped<IIntegrationMessageHandler>(sp => new EconomyMessageHandler(sp.GetRequiredService<EconomyDbContext>(), type));
var app = builder.Build(); app.UseBurmaldaDefaults(); app.MapEconomyEndpoints(); await app.EnsureDatabaseAsync<EconomyDbContext>(); await app.RunAsync();
public partial class Program;
