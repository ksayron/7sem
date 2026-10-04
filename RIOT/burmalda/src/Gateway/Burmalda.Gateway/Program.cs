using System.Threading.RateLimiting;
using Burmalda.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args); builder.Logging.ClearProviders(); builder.Logging.AddJsonConsole(); builder.Services.AddBurmaldaDefaults(builder.Configuration, "api-gateway");
builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
builder.Services.AddCors(options => options.AddPolicy("WebClient", policy => policy
    .WithOrigins(builder.Configuration.GetSection("ClientOrigins").Get<string[]>() ?? ["http://localhost:5300"])
    .AllowAnyHeader()
    .AllowAnyMethod()));
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("api", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("concurrency-lab", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 400, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddHttpClient("monitoring", client => client.Timeout = TimeSpan.FromSeconds(3));
var app = builder.Build(); app.UseCors("WebClient"); app.UseBurmaldaDefaults(); app.UseRateLimiter();
app.Use(async (context, next) => { 
    var correlation = context.Request.Headers["X-Correlation-ID"].FirstOrDefault();
    if (!Guid.TryParse(correlation, out var id))
        id = Guid.NewGuid();
    context.Request.Headers["X-Correlation-ID"] = id.ToString();
    context.Response.Headers["X-Correlation-ID"] = id.ToString();
    await next(); 
});
app.MapGet("/api/admin/system/health", async (IConfiguration configuration, IHttpClientFactory factory, CancellationToken ct) =>
{
    var client = factory.CreateClient("monitoring");
    var names = new[] { "identity", "catalog", "lending", "economy", "reputation", "notifications" };
    var results = new Dictionary<string, object>();
    foreach (var name in names)
    {
        try { using var response = await client.GetAsync($"{configuration[$"ServiceUrls:{name}"]}/health/ready", ct);
            results[name] = new { Healthy = response.IsSuccessStatusCode, Status = (int)response.StatusCode }; }
        catch (Exception ex) {
            results[name] = new { Healthy = false, Error = ex.Message }; 
        }
    }
    return Results.Ok(new { CheckedAt = DateTimeOffset.UtcNow, Services = results });
}).RequireAuthorization("AdminOnly");
app.MapReverseProxy(); await app.RunAsync();
public partial class Program;
