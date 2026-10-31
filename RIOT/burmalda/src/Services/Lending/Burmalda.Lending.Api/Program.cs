using Burmalda.Persistence;
using Burmalda.Contracts;
using Burmalda.Lending.Api;
using Burmalda.ServiceDefaults;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddBurmaldaConsole(builder.Configuration);
builder.Services.AddBurmaldaDefaults(builder.Configuration, "lending-service");
var connection = builder.Configuration.GetConnectionString("Database")!;
builder.Services.AddDbContext<LendingDbContext>(o => o.UseNpgsql(connection).WithDatabaseFailover());
builder.Services.AddServiceMessaging<LendingDbContext, LendingQueue>();
foreach (var type in new[] { MessageNames.BookCopyReserved, MessageNames.BookCopyReservationFailed, MessageNames.BookCopyReleased, MessageNames.PointsReserved, MessageNames.PointsReservationFailed })
    builder.Services.AddScoped<IIntegrationMessageHandler>(sp => new LendingMessageHandler(sp.GetRequiredService<LendingDbContext>(), type));
builder.Services.AddHangfire(x => x.UsePostgreSqlStorage(o => o.UseNpgsqlConnection(connection)));
builder.Services.AddHangfireServer(); builder.Services.AddScoped<LoanJobs>();

var app = builder.Build(); app.UseBurmaldaDefaults(); app.MapLendingEndpoints();
app.UseHangfireDashboard("/hangfire", new DashboardOptions { Authorization = [new HangfireAuthorization()] });
await app.EnsureDatabaseAsync<LendingDbContext>();
RecurringJob.AddOrUpdate<LoanJobs>("detect-overdue", x => x.DetectOverdue(), "*/1 * * * *");
RecurringJob.AddOrUpdate<LoanJobs>("expire-loan-workflows", x => x.ExpireWorkflows(), "*/1 * * * *");
RecurringJob.AddOrUpdate<LoanJobs>("return-reminders", x => x.SendReturnReminders(), "0 * * * *");
await app.RunAsync();

public partial class Program;
