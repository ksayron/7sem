using Burmalda.ConcurrencyLab.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ExperimentStore>();
builder.Services.AddHealthChecks();

var app = builder.Build();
app.MapHealthChecks("/health/ready");

// The intentionally broken operation must never be exposed in a deployed environment.
if (app.Environment.IsDevelopment())
    app.MapConcurrencyLab();

await app.RunAsync();

public partial class Program;
