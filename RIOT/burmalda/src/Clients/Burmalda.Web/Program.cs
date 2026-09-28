using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Burmalda.Web;
using Burmalda.Web.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? "http://localhost:5000";
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(apiBaseUrl) });
builder.Services.AddScoped<AuthSession>();
builder.Services.AddScoped<GatewayApiClient>();

var host = builder.Build();
await host.Services.GetRequiredService<AuthSession>().InitializeAsync();
await host.RunAsync();
