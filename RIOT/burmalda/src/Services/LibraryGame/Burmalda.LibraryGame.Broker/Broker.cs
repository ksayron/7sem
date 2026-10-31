using System.Net.Http.Json;
using Burmalda.LibraryGame;
using Burmalda.LibraryGame.Server;
using Microsoft.EntityFrameworkCore;

namespace Burmalda.LibraryGame.Broker;

public sealed class SubscriptionRow
{
    public string Id { get; set; } = "";
    public string Types { get; set; } = "[]";
    public string Endpoint { get; set; } = "";
}
public sealed class EventRow { public Guid Id { get; set; } public string Json { get; set; } = ""; public int Recipients { get; set; } }
public sealed class DeliveryRow
{
    public long Id { get; set; } public Guid EventId { get; set; } public string Subscriber { get; set; } = "";
    public Guid SessionId { get; set; } public string Endpoint { get; set; } = "";
    public string Status { get; set; } = "Pending"; public int Attempts { get; set; } public long NextAt { get; set; } public string? Error { get; set; }
}
public sealed class BrokerJournalRow { public long Id { get; set; } public string Json { get; set; } = ""; public Guid SessionId { get; set; } }
public sealed class BrokerDb(DbContextOptions<BrokerDb> options) : DbContext(options)
{
    public DbSet<SubscriptionRow> Subscriptions => Set<SubscriptionRow>();
    public DbSet<EventRow> Events => Set<EventRow>();
    public DbSet<DeliveryRow> Deliveries => Set<DeliveryRow>();
    public DbSet<BrokerJournalRow> Journal => Set<BrokerJournalRow>();
    protected override void OnModelCreating(ModelBuilder b) {
        b.Entity<DeliveryRow>().HasIndex(x => new { x.EventId, x.Subscriber }).IsUnique();
        b.Entity<DeliveryRow>().HasIndex(x => new { x.Status, x.NextAt });
        b.Entity<BrokerJournalRow>().HasIndex(x => x.SessionId);
    }
}

public sealed class BrokerStore(IDbContextFactory<BrokerDb> factory, IConfiguration config, ILogger<BrokerStore> logger)
{
    // Serializes short SQLite transactions, never subscriber execution.
    private readonly SemaphoreSlim gate = new(1, 1);
    public string Id => config["Broker:Id"] ?? "A";
    public int Capacity => config.GetValue("Broker:Capacity", 10000);
    public static readonly string[] EventTypes = ["BookPickedUp", "BookShelved", "AbilityUsed", "UpgradePurchased", "AchievementUnlocked"];
    public async Task Subscribe(SubscriberRegistration registration, CancellationToken ct = default)
    {
        var allowed = new[] { "EventLogger", "AchievementTracker", "AbilityHandler", "NotificationSubscriber", "AnalyticsSubscriber" };
        var validEndpoint = allowed.Contains(registration.Id) && registration.Endpoint == $"{LibraryHost.Url(config, "Subscribers")}/internal/subscribers/{registration.Id}";
        validEndpoint |= Id == "A" && registration.Id == "bridge-B" && registration.Endpoint == $"{LibraryHost.Url(config, "BrokerB")}/internal/events";
        if (!validEndpoint || registration.EventTypes.Length == 0 || registration.EventTypes.Except(EventTypes).Any()) throw new GameRuleException("Неизвестная подписка или адрес обработчика.");
        await gate.WaitAsync(ct);
        try {
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.Subscriptions.FindAsync([registration.Id], ct);
            if (row is null) { row = new() { Id = registration.Id }; db.Add(row); }
            row.Types = GameJson.Write(registration.EventTypes.Distinct().ToArray()); row.Endpoint = registration.Endpoint;
            await db.SaveChangesAsync(ct);
        } finally { gate.Release(); }
    }
    public async Task<BrokerReceipt> Publish(GameEvent message, CancellationToken ct = default)
    {
        if (message.EventId == Guid.Empty || message.SessionId == Guid.Empty || message.UserId == Guid.Empty || !EventTypes.Contains(message.EventType)) throw new GameRuleException("Некорректное событие.");
        await gate.WaitAsync(ct);
        try {
            await using var db = await factory.CreateDbContextAsync(ct);
            var previous = await db.Events.FindAsync([message.EventId], ct);
            if (previous is not null) return new(message.EventId, previous.Recipients, true);
            var recipients = (await db.Subscriptions.ToListAsync(ct)).Where(x => GameJson.Read<string[]>(x.Types).Contains(message.EventType)).ToArray();
            if (await db.Deliveries.CountAsync(x => x.Status == "Pending" || x.Status == "InFlight", ct) + recipients.Length > Capacity)
                throw new GameRuleException("Очередь брокера заполнена; повторите публикацию.", 503);
            db.Events.Add(new() { Id = message.EventId, Json = GameJson.Write(message), Recipients = recipients.Length });
            foreach (var sub in recipients) db.Deliveries.Add(new() { EventId = message.EventId, SessionId = message.SessionId, Subscriber = sub.Id, Endpoint = sub.Endpoint });
            AddJournal(db, message, "", "EVENT RECEIVED", null);
            await db.SaveChangesAsync(ct);
            return new(message.EventId, recipients.Length);
        } finally { gate.Release(); }
    }
    private void AddJournal(BrokerDb db, GameEvent e, string subscriber, string result, string? error)
    {
        db.Journal.Add(new() { SessionId = e.SessionId, Json = GameJson.Write(new JournalEntry(0, DateTimeOffset.UtcNow, e.UserId, e.SessionId,
            e.ClientId, e.EventType, e.EventId, e.EventType == "AchievementUnlocked" ? "AchievementTracker" : "LibraryGame API", Id, subscriber, result, error)) });
        logger.LogInformation("Broker {Broker} {EventType} {EventId} → {Subscriber}: {Result} {Error}", Id, e.EventType, e.EventId, subscriber, result, error);
    }
    public async Task<List<(DeliveryRow Row, GameEvent Event)>> Claim(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try {
            await using var db = await factory.CreateDbContextAsync(ct);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var candidates = await db.Deliveries.Where(x => x.Status == "Pending" && x.NextAt <= now &&
                !db.Deliveries.Any(p => p.Subscriber == x.Subscriber && p.SessionId == x.SessionId && p.Id < x.Id && (p.Status == "Pending" || p.Status == "InFlight")))
                .OrderBy(x => x.Id).Take(24).ToListAsync(ct);
            var result = new List<(DeliveryRow, GameEvent)>();
            foreach (var row in candidates) {
                row.Status = "InFlight"; row.Attempts++;
                var e = GameJson.Read<GameEvent>((await db.Events.FindAsync([row.EventId], ct))!.Json);
                result.Add((row, e)); AddJournal(db, e, row.Subscriber, $"START #{row.Attempts}", null);
            }
            await db.SaveChangesAsync(ct); return result;
        } finally { gate.Release(); }
    }
    public async Task Finish(long id, GameEvent e, string? error, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try {
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = (await db.Deliveries.FindAsync([id], ct))!;
            row.Error = error;
            row.Status = error is null ? "Success" : row.Attempts >= 5 ? "DeadLetter" : "Pending";
            row.NextAt = DateTimeOffset.UtcNow.AddSeconds(Math.Pow(2, Math.Min(row.Attempts - 1, 3))).ToUnixTimeMilliseconds();
            AddJournal(db, e, row.Subscriber, error is null ? "SUCCESS" : row.Status == "DeadLetter" ? "DEAD LETTER" : "RETRY", error);
            await db.SaveChangesAsync(ct);
        } finally { gate.Release(); }
    }
    public async Task Recover(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Deliveries.Where(x => x.Status == "InFlight").ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Pending"), ct);
    }
    public async Task<int> Replay(Guid session, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try {
            await using var db = await factory.CreateDbContextAsync(ct);
            return await db.Deliveries.Where(x => x.SessionId == session && x.Status == "DeadLetter").ExecuteUpdateAsync(x => x
                .SetProperty(p => p.Status, "Pending").SetProperty(p => p.Attempts, 0).SetProperty(p => p.NextAt, 0L).SetProperty(p => p.Error, (string?)null), ct);
        } finally { gate.Release(); }
    }
    public async Task<BrokerSnapshot> Snapshot(Guid session, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try {
            await using var db = await factory.CreateDbContextAsync(ct);
            var subs = (await db.Subscriptions.AsNoTracking().ToListAsync(ct)).Select(x => new SubscriberRegistration(x.Id, GameJson.Read<string[]>(x.Types), x.Endpoint)).ToList();
            var rows = await db.Deliveries.AsNoTracking().Where(x => x.SessionId == session).OrderByDescending(x => x.Id).Take(200).ToListAsync(ct);
            var ids = rows.Select(x => x.EventId).Distinct().ToArray();
            var events = (await db.Events.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(ct)).ToDictionary(x => x.Id, x => GameJson.Read<GameEvent>(x.Json));
            var deliveries = rows.Select(x => { var e = events[x.EventId]; return new DeliveryView(x.Id, x.EventId, e.EventType, e.UserId, e.SessionId, x.Subscriber, x.Status, x.Attempts, DateTimeOffset.FromUnixTimeMilliseconds(x.NextAt), x.Error); }).ToList();
            var journal = (await db.Journal.AsNoTracking().Where(x => x.SessionId == session).OrderByDescending(x => x.Id).Take(300).ToListAsync(ct)).Select(x => GameJson.Read<JournalEntry>(x.Json) with { Id = x.Id }).ToList();
            return new(Id, subs, deliveries, journal);
        } finally { gate.Release(); }
    }
}

public sealed class DeliveryWorker(BrokerStore store, IHttpClientFactory clients, ILogger<DeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await store.Recover(stoppingToken);
        var running = new List<Task>();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try {
            do {
                running.RemoveAll(x => x.IsCompleted);
                try {
                    if (running.Count < 48) foreach (var item in await store.Claim(stoppingToken)) running.Add(Deliver(item.Row, item.Event, stoppingToken));
                } catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogWarning("Delivery scheduler: {Error}", ex.Message); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        await Task.WhenAll(running);
    }
    private async Task Deliver(DeliveryRow row, GameEvent message, CancellationToken ct)
    {
        string? error = null;
        try {
            using var response = await clients.CreateClient("library-internal").PostAsJsonAsync(row.Endpoint, message, ct);
            if (!response.IsSuccessStatusCode) error = $"HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}";
        } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { error = ex.Message; }
        if (ct.IsCancellationRequested) return; // Persisted InFlight is recovered at next start.
        try { await store.Finish(row.Id, message, error, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { logger.LogError("Delivery completion could not be persisted: {Error}", ex.Message); }
    }
}
