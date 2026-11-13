using Burmalda.Persistence;
using Burmalda.Contracts;
using Burmalda.Reputation.Api;
using Burmalda.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args); builder.Logging.AddBurmaldaConsole(builder.Configuration); builder.Services.AddBurmaldaDefaults(builder.Configuration, "reputation-service");
builder.Services.AddDbContext<ReputationDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Database")).WithDatabaseFailover()); builder.Services.AddServiceMessaging<ReputationDbContext, ReputationQueue>();
foreach (var type in new[] { MessageNames.UserRegistered, MessageNames.LoanCompleted, MessageNames.LoanBecameOverdue, MessageNames.LoanCancelled }) builder.Services.AddScoped<IIntegrationMessageHandler>(sp => new ReputationMessageHandler(sp.GetRequiredService<ReputationDbContext>(), type));
builder.Services.AddBusinessReadiness<ReputationDbContext>();

var app = builder.Build(); app.UseBurmaldaDefaults(); app.MapReputationEndpoints(); await app.EnsureDatabaseAsync<ReputationDbContext>(); await app.RunAsync();
public partial class Program;
