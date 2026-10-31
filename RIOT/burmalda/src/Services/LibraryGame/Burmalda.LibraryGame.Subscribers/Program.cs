using Burmalda.Persistence;
using Burmalda.LibraryGame;
using Burmalda.LibraryGame.Server;
using Burmalda.LibraryGame.Subscribers;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddLibraryHost(builder.Configuration);
builder.Services.AddDbContextFactory<SubscriberDb>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Database")).WithDatabaseFailover());
builder.Services.AddSingleton<SubscriberHandlers>(); builder.Services.AddSingleton<HttpPublisher>();
builder.Services.AddHostedService<SubscriptionWorker>(); builder.Services.AddHostedService<OutboxWorker<SubscriberDb>>();
var app = builder.Build(); app.UseLibraryHost();
await using (var db = await app.Services.GetRequiredService<IDbContextFactory<SubscriberDb>>().CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();
app.MapPost("/internal/subscribers/{id}", async (string id, GameEvent e, SubscriberHandlers handlers, CancellationToken ct) => { await handlers.Handle(id, e, ct); return Results.Ok(new { processed = e.EventId, subscriber = id }); });
app.MapGet("/internal/projections/{session:guid}", (Guid session, Guid user, SubscriberHandlers handlers, CancellationToken ct) => handlers.Projection(session, user, ct));
if (app.Environment.IsDevelopment()) app.MapPost("/internal/control", async (ControlRequest request, SubscriberHandlers handlers, CancellationToken ct) => { await handlers.Control(request, ct); return Results.Ok(); });
await app.RunAsync();
