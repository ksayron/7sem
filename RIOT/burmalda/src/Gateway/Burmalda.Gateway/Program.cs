using System.Threading.RateLimiting;
using Burmalda.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args); builder.Logging.AddBurmaldaConsole(builder.Configuration); builder.Services.AddBurmaldaDefaults(builder.Configuration, "api-gateway");
var proxyConfiguration = builder.Configuration;
if (!builder.Configuration.GetValue<bool>("Features:LearningLabs"))
{
    var entries = builder.Configuration.GetSection("ReverseProxy").AsEnumerable()
        .Where(x => x.Value is not null && !x.Key.Split(':').Any(k => k is "raft-lab" or "concurrency-lab" or "library-game"))
        .ToDictionary(x => x.Key, x => x.Value);
    proxyConfiguration = new ConfigurationManager();
    proxyConfiguration.AddInMemoryCollection(entries);
}
builder.Services.AddReverseProxy().LoadFromConfig(proxyConfiguration.GetSection("ReverseProxy"))
    .ConfigureHttpClient((_, handler) => handler.PooledConnectionLifetime = TimeSpan.FromSeconds(10));
builder.Services.AddCors(options => options.AddPolicy("WebClient", policy => policy
    .WithOrigins(builder.Configuration.GetSection("ClientOrigins").Get<string[]>() ?? ["http://localhost:5300"])
    .AllowAnyHeader()
    .AllowAnyMethod()));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delay))
            context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(delay.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await context.HttpContext.Response.WriteAsJsonAsync(new { message = "Слишком много запросов. Повторите чуть позже." }, ct);
    };
    options.AddPolicy("api", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("concurrency-lab", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 1000, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
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
