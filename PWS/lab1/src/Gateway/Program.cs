using StackApi;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

var builder = WebApplication.CreateBuilder(args);
var mode = builder.Configuration["GATEWAY_MODE"] ?? "sticky";
if (mode is not ("sticky" or "round-robin"))
    throw new InvalidOperationException("GATEWAY_MODE must be sticky or round-robin.");
var sticky = mode == "sticky";
var destinations = new Dictionary<string, DestinationConfig>
{
    ["replica-a"] = new() { Address = builder.Configuration["BACKEND_A"] ?? (sticky ? "http://localhost:8111/" : "http://localhost:8121/") },
    ["replica-b"] = new() { Address = builder.Configuration["BACKEND_B"] ?? (sticky ? "http://localhost:8112/" : "http://localhost:8122/") }
};
builder.Services.AddReverseProxy().LoadFromMemory(
    [new RouteConfig { RouteId = "stack", ClusterId = "stack-cluster", Match = new RouteMatch { Path = "/stack" } }],
    [new ClusterConfig
    {
        ClusterId = "stack-cluster",
        Destinations = destinations,
        LoadBalancingPolicy = "RoundRobin",
        SessionAffinity = new SessionAffinityConfig
        {
            Enabled = sticky,
            Policy = "HashCookie",
            FailurePolicy = "Return503Error",
            AffinityKeyName = "WS01.Affinity",
            Cookie = new SessionAffinityCookieConfig
            {
                HttpOnly = true, IsEssential = true, Path = "/stack",
                SameSite = SameSiteMode.Strict, SecurePolicy = CookieSecurePolicy.SameAsRequest
            }
        },
        HttpRequest = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromSeconds(15) }
    }]);
var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Gateway-Policy"] = mode;
    await next();
});
app.MapDemo(sticky ? "server" : "cookie", sticky ? "Sticky Session Gateway" : "Round Robin Gateway");
app.MapReverseProxy();
app.Run();
