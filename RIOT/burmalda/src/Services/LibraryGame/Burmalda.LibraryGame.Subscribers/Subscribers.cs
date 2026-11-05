using System.Net.Http.Json;
using Burmalda.LibraryGame;
using Burmalda.LibraryGame.Server;
using Microsoft.EntityFrameworkCore;

namespace Burmalda.LibraryGame.Subscribers;

public sealed class InboxRow { public long Id { get; set; } public Guid EventId { get; set; } public Guid SessionId { get; set; } public string Subscriber { get; set; } = ""; }
public sealed class AchievementRow { public long Id { get; set; } public Guid UserId { get; set; } public Guid SessionId { get; set; } public string Key { get; set; } = ""; public DateTimeOffset CreatedAt { get; set; } }
public sealed class NoticeRow { public long Id { get; set; } public Guid UserId { get; set; } public Guid SessionId { get; set; } public string Text { get; set; } = ""; public DateTimeOffset CreatedAt { get; set; } }
public sealed class AnalysisRow { public long Id { get; set; } public Guid UserId { get; set; } public Guid SessionId { get; set; } public string Type { get; set; } = ""; public int Count { get; set; } }
public sealed class ControlRow { public long Id { get; set; } public Guid SessionId { get; set; } public string Subscriber { get; set; } = ""; public string Json { get; set; } = ""; }
public sealed class EventLogRow { public long Id { get; set; } public Guid EventId { get; set; } public Guid SessionId { get; set; } public string Json { get; set; } = ""; }
public sealed class SubscriberDb(DbContextOptions<SubscriberDb> options) : DbContext(options), IOutboxDatabase
{
    public DbSet<InboxRow> Inbox => Set<InboxRow>(); public DbSet<AchievementRow> Achievements => Set<AchievementRow>();
    public DbSet<NoticeRow> Notices => Set<NoticeRow>(); public DbSet<AnalysisRow> Analytics => Set<AnalysisRow>();
    public DbSet<ControlRow> Controls => Set<ControlRow>(); public DbSet<EventLogRow> Logs => Set<EventLogRow>();
    public DbSet<OutgoingEvent> Outgoing => Set<OutgoingEvent>();
    protected override void OnModelCreating(ModelBuilder b) {
        b.Entity<InboxRow>().HasIndex(x => new { x.EventId, x.Subscriber }).IsUnique();
        b.Entity<AchievementRow>().HasIndex(x => new { x.SessionId, x.Key }).IsUnique();
        b.Entity<AnalysisRow>().HasIndex(x => new { x.SessionId, x.Type }).IsUnique();
        b.Entity<ControlRow>().HasIndex(x => new { x.SessionId, x.Subscriber }).IsUnique();
        b.Entity<OutgoingEvent>().HasIndex(x => x.EventId).IsUnique();
    }
}
public sealed class SubscriberHandlers(IDbContextFactory<SubscriberDb> factory, IHttpClientFactory clients, IConfiguration config, ILogger<SubscriberHandlers> logger)
{
    private readonly SemaphoreSlim commitGate = new(1, 1);
    public static readonly Dictionary<string, string[]> Subscriptions = new() {
        ["EventLogger"] = ["BookPickedUp", "BookShelved", "AbilityUsed", "UpgradePurchased", "AchievementUnlocked"],
        ["AchievementTracker"] = ["BookShelved", "AbilityUsed"], ["AbilityHandler"] = ["AbilityUsed"],
        ["NotificationSubscriber"] = ["AchievementUnlocked", "UpgradePurchased"],
        ["AnalyticsSubscriber"] = ["BookPickedUp", "BookShelved", "AbilityUsed", "UpgradePurchased"] };
    public async Task Handle(string id, GameEvent e, CancellationToken ct)
    {
        if (!Subscriptions.TryGetValue(id, out var types) || !types.Contains(e.EventType)) throw new GameRuleException("Подписчик не зарегистрирован на этот тип события.");
        await using (var readDb = await factory.CreateDbContextAsync(ct)) {
            if (await readDb.Inbox.AnyAsync(x => x.EventId == e.EventId && x.Subscriber == id, ct)) return;
            var control = e.Demo ? await readDb.Controls.SingleOrDefaultAsync(x => x.SessionId == e.SessionId && x.Subscriber == id, ct) : null;
            var setting = control is null ? new SubscriberControl() : GameJson.Read<SubscriberControl>(control.Json);
            logger.LogInformation("{Subscriber} START {Type} {EventId} at {Time}", id, e.EventType, e.EventId, DateTimeOffset.UtcNow);
            if (!setting.Available) throw new GameRuleException($"{id} UNAVAILABLE", 503);
            if (setting.DelayMs > 0) await Task.Delay(setting.DelayMs, ct);
            if (setting.Throw) throw new GameRuleException($"{id}: искусственное исключение обработки", 503);
        }
        if (id == "AbilityHandler") {
            using var response = await clients.CreateClient("library-internal").PostAsJsonAsync($"{LibraryHost.Url(config, "GameApi")}/internal/effects", new EffectRequest(e), ct);
            response.EnsureSuccessStatusCode();
        }
        await commitGate.WaitAsync(ct);
        try {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (await db.Inbox.AnyAsync(x => x.EventId == e.EventId && x.Subscriber == id, ct)) return;
            switch (id)
            {
                case "EventLogger": db.Logs.Add(new() { EventId = e.EventId, SessionId = e.SessionId, Json = GameJson.Write(e) }); break;
                case "AchievementTracker":
                    var keys = new List<string>();
                    if (e.EventType == "BookShelved" && e.Data.TotalShelved >= 1) keys.Add("first-book");
                    if (e.EventType == "BookShelved" && e.Data.SeriesShelved == 12) keys.Add("first-series");
                    if (e.EventType == "BookShelved" && e.Data.TotalShelved == 72) keys.Add("librarian");
                    if (e.EventType == "AbilityUsed") keys.Add("first-attraction");
                    foreach (var key in keys) {
                        if (await db.Achievements.AnyAsync(x => x.SessionId == e.SessionId && x.Key == key, ct)) continue;
                        db.Achievements.Add(new() { UserId = e.UserId, SessionId = e.SessionId, Key = key, CreatedAt = DateTimeOffset.UtcNow });
                        var unlocked = e with { EventId = Guid.NewGuid(), EventType = "AchievementUnlocked", EntityId = key, Timestamp = DateTimeOffset.UtcNow, Data = new(Achievement: key) };
                        db.Outgoing.Add(new() { EventId = unlocked.EventId, Json = GameJson.Write(unlocked), Target = "BrokerA" });
                    }
                    break;
                case "NotificationSubscriber":
                    db.Notices.Add(new() { UserId = e.UserId, SessionId = e.SessionId,
                        Text = e.EventType == "AchievementUnlocked" ? $"Достижение: {AchievementName(e.Data.Achievement!)}" : "Улучшение библиотеки приобретено", CreatedAt = DateTimeOffset.UtcNow });
                    break;
                case "AnalyticsSubscriber":
                    var row = await db.Analytics.SingleOrDefaultAsync(x => x.SessionId == e.SessionId && x.Type == e.EventType, ct);
                    if (row is null) { row = new() { UserId = e.UserId, SessionId = e.SessionId, Type = e.EventType }; db.Analytics.Add(row); }
                    row.Count++;
                    break;
            }
            db.Inbox.Add(new() { EventId = e.EventId, SessionId = e.SessionId, Subscriber = id });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            logger.LogInformation("{Subscriber} SUCCESS {EventId} at {Time}", id, e.EventId, DateTimeOffset.UtcNow);
        } finally { commitGate.Release(); }
    }
    public static string AchievementName(string key) => key switch { "first-book" => "Первая страница", "first-series" => "Собрание сочинений", "first-attraction" => "Книги, ко мне!", "librarian" => "Хранитель библиотеки", _ => key };
    public async Task<ProjectionView> Projection(Guid session, Guid user, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var achievements = await db.Achievements.AsNoTracking().Where(x => x.SessionId == session && x.UserId == user).OrderBy(x => x.CreatedAt).Select(x => new AchievementView(x.Key, x.CreatedAt)).ToListAsync(ct);
        var notices = await db.Notices.AsNoTracking().Where(x => x.SessionId == session && x.UserId == user).OrderByDescending(x => x.CreatedAt).Take(15).Select(x => new NoticeView(x.Text, x.CreatedAt)).ToListAsync(ct);
        var analysis = await db.Analytics.AsNoTracking().Where(x => x.SessionId == session && x.UserId == user).ToDictionaryAsync(x => x.Type, x => x.Count, ct);
        var controls = (await db.Controls.AsNoTracking().Where(x => x.SessionId == session).ToListAsync(ct)).ToDictionary(x => x.Subscriber, x => GameJson.Read<SubscriberControl>(x.Json));
        return new(achievements, notices, analysis, controls);
    }
    public async Task Control(ControlRequest r, CancellationToken ct)
    {
        if (!Subscriptions.ContainsKey(r.Subscriber) || r.Control.DelayMs is < 0 or > 5000) throw new GameRuleException("Неизвестный подписчик или задержка вне 0–5000 мс.");
        await commitGate.WaitAsync(ct);
        try {
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.Controls.SingleOrDefaultAsync(x => x.SessionId == r.SessionId && x.Subscriber == r.Subscriber, ct);
            if (row is null) { row = new() { SessionId = r.SessionId, Subscriber = r.Subscriber }; db.Controls.Add(row); }
            row.Json = GameJson.Write(r.Control); await db.SaveChangesAsync(ct);
        } finally { commitGate.Release(); }
    }
}
public sealed class SubscriptionWorker(IHttpClientFactory clients, IConfiguration config, ILogger<SubscriptionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do {
            foreach (var id in SubscriberHandlers.Subscriptions.Keys.Where(x => x != "AnalyticsSubscriber")) {
                try {
                    var broker = id == "NotificationSubscriber" ? "BrokerB" : "BrokerA";
                    using var response = await clients.CreateClient("library-internal").PostAsJsonAsync($"{LibraryHost.Url(config, broker)}/internal/subscriptions",
                        new SubscriberRegistration(id, SubscriberHandlers.Subscriptions[id], $"{LibraryHost.Url(config, "Subscribers")}/internal/subscribers/{id}"), stoppingToken);
                    response.EnsureSuccessStatusCode();
                } catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogWarning("Subscription {Subscriber} waiting: {Error}", id, ex.Message); }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
