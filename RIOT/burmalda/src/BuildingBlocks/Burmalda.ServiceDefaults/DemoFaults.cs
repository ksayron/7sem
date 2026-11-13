using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Burmalda.ServiceDefaults;

public sealed record DemoFault(string Service, int ErrorPercent, int DelayMs, DateTimeOffset ExpiresAt)
{
    public bool IsActive(DateTimeOffset now) => now < ExpiresAt && (ErrorPercent > 0 || DelayMs > 0);
}

public sealed class DemoFaultState
{
    private DemoFault? current;
    public DemoFault? Current => Volatile.Read(ref current);
    public void Set(DemoFault? fault) => Volatile.Write(ref current, fault);
}

public sealed class DemoFaultPoller(IHttpClientFactory clients, IConfiguration configuration, DemoFaultState state,
    ILogger<DemoFaultPoller> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var url = configuration["DemoFaults:ControlUrl"] ?? "http://demo-control:8080";
        var service = configuration["DemoFaults:Service"] ?? "unknown";
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{url}/internal/faults/{Uri.EscapeDataString(service)}");
                request.Headers.Add("X-Demo-Key", configuration["DemoFaults:Key"]);
                using var response = await clients.CreateClient("demo-faults").SendAsync(request, stoppingToken);
                response.EnsureSuccessStatusCode();
                var fault = await response.Content.ReadFromJsonAsync<DemoFault>(stoppingToken);
                // Reject unbounded/invalid remote configuration; an expired snapshot is always harmless.
                if (fault is not null && fault.Service == service && fault.ErrorPercent is >= 0 and <= 100 &&
                    fault.DelayMs is >= 0 and <= 5000 && fault.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(125)) state.Set(fault);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Demo fault configuration unavailable; local expiry remains authoritative");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}

public sealed class DemoFaultMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, DemoFaultState state)
    {
        // Probes, control endpoints and static assets must never be faulted.
        var fault = state.Current;
        if (context.Request.Path.StartsWithSegments("/api") && fault?.IsActive(DateTimeOffset.UtcNow) == true)
        {
            if (fault.DelayMs > 0) await Task.Delay(fault.DelayMs, context.RequestAborted);
            if (fault.IsActive(DateTimeOffset.UtcNow) && Random.Shared.Next(100) < fault.ErrorPercent)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers["X-Burmalda-Demo-Fault"] = fault.Service;
                await context.Response.WriteAsJsonAsync(new { message = "Временный демонстрационный сбой API", fault.ExpiresAt }, context.RequestAborted);
                return;
            }
        }
        await next(context);
    }
}
