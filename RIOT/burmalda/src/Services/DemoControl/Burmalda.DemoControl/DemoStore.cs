using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Burmalda.DemoControl;

public sealed class StoredValue { public string Key { get; set; } = ""; public string Json { get; set; } = ""; }
public sealed class StoredEvent
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public string Kind { get; set; } = "";
    public string Message { get; set; } = "";
}
public sealed class DemoDb(DbContextOptions<DemoDb> options) : DbContext(options)
{
    public DbSet<StoredValue> Values => Set<StoredValue>();
    public DbSet<StoredEvent> Events => Set<StoredEvent>();
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<StoredValue>().HasKey(x => x.Key);
}

public sealed class DemoMetrics : IDisposable
{
    public readonly Meter Meter = new("Burmalda.Demo");
    public Counter<long> Requests { get; }
    public Histogram<double> Duration { get; }
    public Counter<long> Operations { get; }
    public Counter<long> Events { get; }
    public DemoMetrics()
    {
        Requests = Meter.CreateCounter<long>("burmalda_demo_requests");
        Duration = Meter.CreateHistogram<double>("burmalda_demo_request_duration", "s");
        Operations = Meter.CreateCounter<long>("burmalda_demo_operations");
        Events = Meter.CreateCounter<long>("burmalda_demo_events");
    }
    public void Dispose() => Meter.Dispose();
}

public sealed class DemoStore(IDbContextFactory<DemoDb> factory, DemoMetrics metrics)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task Initialize()
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        var run = await Read<DemoRun>("run");
        if (run?.State is "running" or "draining")
        {
            var interrupted = run with { State = "interrupted", FinishedAt = DateTimeOffset.UtcNow };
            await Write("run", interrupted);
            await Write("result:" + run.Id, new { Run = interrupted,
                Unresolved = await Read<Dictionary<string, string>>("pending") ?? [] });
            await Event("interrupted", "Предыдущий запуск прерван перезапуском симулятора. Незавершённые операции сохранены для проверки.");
        }
        // Faults are deliberately not restored: every process starts healthy.
    }
    public async Task<T?> Read<T>(string key)
    {
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.Values.AsNoTracking().SingleOrDefaultAsync(x => x.Key == key);
        return row is null ? default : JsonSerializer.Deserialize<T>(row.Json);
    }
    public async Task Write<T>(string key, T value)
    {
        await gate.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var row = await db.Values.FindAsync(key);
            if (row is null) db.Values.Add(new StoredValue { Key = key, Json = JsonSerializer.Serialize(value) });
            else row.Json = JsonSerializer.Serialize(value);
            await db.SaveChangesAsync();
        }
        finally { gate.Release(); }
    }
    public async Task Event(string kind, string message)
    {
        await gate.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            db.Events.Add(new StoredEvent { At = DateTimeOffset.UtcNow, Kind = kind, Message = message });
            await db.SaveChangesAsync();
            // Keep journal bounded without deleting run results or fixtures.
            if (await db.Events.CountAsync() > 10000)
                await db.Events.Where(x => x.Id < db.Events.Max(y => y.Id) - 9000).ExecuteDeleteAsync();
        }
        finally { gate.Release(); }
        metrics.Events.Add(1, new KeyValuePair<string, object?>("event", kind));
    }
    public async Task<DemoEvent[]> Events()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Events.AsNoTracking().OrderByDescending(x => x.Id).Take(100)
            .Select(x => new DemoEvent(x.Id, x.At, x.Kind, x.Message)).ToArrayAsync();
    }
}
