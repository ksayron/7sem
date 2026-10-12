using Burmalda.Contracts;
using Burmalda.Notifications.Api;
using Burmalda.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args); builder.Logging.AddBurmaldaConsole(builder.Configuration); builder.Services.AddBurmaldaDefaults(builder.Configuration, "notification-service");
builder.Services.AddDbContext<NotificationDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Database"))); builder.Services.AddServiceMessaging<NotificationDbContext, NotificationQueue>();
foreach (var type in new[] { MessageNames.UserRegistered, MessageNames.LoanRequested, MessageNames.LoanAccepted, MessageNames.LoanRejected, MessageNames.LoanCancelled, MessageNames.LoanActivated, MessageNames.ReturnInitiated, MessageNames.LoanCompleted, MessageNames.LoanBecameOverdue, MessageNames.FineCreated, MessageNames.ReturnReminderDue }) builder.Services.AddScoped<IIntegrationMessageHandler>(sp => new NotificationMessageHandler(sp.GetRequiredService<NotificationDbContext>(), type));
var app = builder.Build(); app.UseBurmaldaDefaults(); app.MapNotificationEndpoints(); await app.EnsureDatabaseAsync<NotificationDbContext>(); await app.RunAsync();
public partial class Program;
