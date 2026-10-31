using Burmalda.Persistence;
using System.Net.Http.Json;
using Burmalda.LibraryGame;
using Burmalda.LibraryGame.Api;
using Burmalda.LibraryGame.Server;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddLibraryHost(builder.Configuration, jwt: true);
builder.Services.AddDbContextFactory<GameDb>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Database")).WithDatabaseFailover());
builder.Services.AddSingleton<GameStore>(); builder.Services.AddSingleton<HttpPublisher>();
builder.Services.AddHostedService<OutboxWorker<GameDb>>();
var app = builder.Build(); app.UseLibraryHost(jwt: true);
await using (var db = await app.Services.GetRequiredService<IDbContextFactory<GameDb>>().CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();
var api = app.MapGroup("/api/library-game").RequireAuthorization();
async Task<GameState> Current(HttpContext c, bool demo, GameStore store, CancellationToken ct) {
    if (demo && !app.Environment.IsDevelopment()) throw new GameRuleException("Лабораторный режим доступен только в Development.", 404);
    return await store.Current(c.UserId(), demo, ct);
}
api.MapGet("/state", async (HttpContext c, bool? demo, GameStore store, IHttpClientFactory clients, CancellationToken ct) => {
    var state = await Current(c, demo ?? false, store, ct);
    ProjectionView projection;
    try { projection = (await clients.CreateClient("library-read").GetFromJsonAsync<ProjectionView>($"{LibraryHost.Url(app.Configuration, "Subscribers")}/internal/projections/{state.SessionId}?user={state.UserId}", ct))!; }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { projection = new([], [], [], [], false); }
    return new GameSnapshot(state, projection);
});
api.MapPost("/commands", async (HttpContext c, bool? demo, GameCommand command, GameStore store, CancellationToken ct) => {
    await Current(c, demo ?? false, store, ct); return await store.Execute(c.UserId(), demo ?? false, command, ct);
});
api.MapPost("/reset", async (HttpContext c, bool? demo, ResetRequest request, GameStore store, CancellationToken ct) => {
    await Current(c, demo ?? false, store, ct); return await store.Reset(c.UserId(), demo ?? false, request.SessionId, ct);
});
app.MapPost("/internal/effects", (EffectRequest request, GameStore store, CancellationToken ct) => store.Apply(request.Event, ct));
if (app.Environment.IsDevelopment()) {
    api.MapPost("/retry-delivery", async (HttpContext c, bool? demo, GameStore store, IHttpClientFactory clients, CancellationToken ct) => {
        var state = await store.Current(c.UserId(), demo ?? false, ct); var client = clients.CreateClient("library-internal");
        foreach (var broker in new[] { "BrokerA", "BrokerB" }) {
            using var response = await client.PostAsync($"{LibraryHost.Url(app.Configuration, broker)}/internal/replay/{state.SessionId}", null, ct);
            response.EnsureSuccessStatusCode();
        }
        return Results.Ok();
    });
    api.MapGet("/lab", async (HttpContext c, GameStore store, IHttpClientFactory clients, CancellationToken ct) => {
        var state = await store.Current(c.UserId(), true, ct);
        var client = clients.CreateClient("library-internal");
        async Task<object> Snapshot(string name) {
            try { return (await client.GetFromJsonAsync<BrokerSnapshot>($"{LibraryHost.Url(app.Configuration, name)}/internal/snapshot/{state.SessionId}", ct))!; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { return new { brokerId = name, available = false, error = "Брокер недоступен" }; }
        }
        await using var db = await app.Services.GetRequiredService<IDbContextFactory<GameDb>>().CreateDbContextAsync(ct);
        return Results.Ok(new { state.SessionId, brokers = await Task.WhenAll(Snapshot("BrokerA"), Snapshot("BrokerB")), outboxPending = await db.Outgoing.CountAsync(x => x.Json.Contains(state.SessionId.ToString()), ct) });
    });
    api.MapPost("/lab/control", async (HttpContext c, LabControlRequest request, GameStore store, IHttpClientFactory clients, CancellationToken ct) => {
        var state = await store.Current(c.UserId(), true, ct);
        using var response = await clients.CreateClient("library-internal").PostAsJsonAsync($"{LibraryHost.Url(app.Configuration, "Subscribers")}/internal/control", new ControlRequest(state.SessionId, request.Subscriber, request.Control), ct);
        return Results.Content(await response.Content.ReadAsStringAsync(ct), "application/json", statusCode: (int)response.StatusCode);
    });
    api.MapPost("/lab/analytics", async (IHttpClientFactory clients, CancellationToken ct) => {
        using var response = await clients.CreateClient("library-internal").PostAsJsonAsync($"{LibraryHost.Url(app.Configuration, "BrokerB")}/internal/subscriptions",
            new SubscriberRegistration("AnalyticsSubscriber", ["BookPickedUp", "BookShelved", "AbilityUsed", "UpgradePurchased"], $"{LibraryHost.Url(app.Configuration, "Subscribers")}/internal/subscribers/AnalyticsSubscriber"), ct);
        response.EnsureSuccessStatusCode(); return Results.Ok();
    });
    api.MapPost("/lab/replay", async (HttpContext c, GameStore store, IHttpClientFactory clients, CancellationToken ct) => {
        var state = await store.Current(c.UserId(), true, ct); var client = clients.CreateClient("library-internal");
        foreach (var broker in new[] { "BrokerA", "BrokerB" }) { using var response = await client.PostAsync($"{LibraryHost.Url(app.Configuration, broker)}/internal/replay/{state.SessionId}", null, ct); response.EnsureSuccessStatusCode(); }
        return Results.Ok();
    });
}
await app.RunAsync();

