using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

var baseUrl = Environment.GetEnvironmentVariable("BURMALDA_LAB_URL") ?? "http://localhost:5107";
using var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 32 })
{ BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
var action = args.ElementAtOrDefault(0)?.ToLowerInvariant() ?? "compare";

try
{
    switch (action)
    {
        case "compare":
        {
            var count = ParseInt(1, 12, 2, 32);
            var delay = ParseInt(2, 60, 10, 200);
            var results = new List<RunReport>();
            var baseline = await Create("Unsafe", count, delay, false);
            var baselineWatch = Stopwatch.StartNew();
            for (var i = 1; i <= count; i++) await Send(baseline.Id, 1, $"sequential-{i:00}");
            baselineWatch.Stop();
            var baselineSnapshot = await Get(baseline.Id);
            results.Add(new RunReport("Unsafe sequential", baselineWatch.ElapsedMilliseconds, baselineSnapshot));
            Print(baselineSnapshot, baselineWatch.ElapsedMilliseconds, "Unsafe sequential");
            foreach (var mode in new[] { "Unsafe", "Lock", "SemaphoreSlim" })
            {
                var created = await Create(mode, count, delay);
                var watch = Stopwatch.StartNew();
                await Send(created.Id, count, "cli");
                watch.Stop();
                var snapshot = await Get(created.Id);
                results.Add(new RunReport(mode, watch.ElapsedMilliseconds, snapshot));
                Print(snapshot, watch.ElapsedMilliseconds);
            }
            var reportDirectory = Path.Combine(Directory.GetCurrentDirectory(), "lab-results");
            Directory.CreateDirectory(reportDirectory);
            var reportFile = Path.Combine(reportDirectory,
                $"concurrency-lab-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
            await File.WriteAllTextAsync(reportFile, JsonSerializer.Serialize(new
            {
                recordedAtUtc = DateTimeOffset.UtcNow, server = baseUrl, clients = count,
                workDelayMs = delay, runs = results
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            Console.WriteLine($"JSON report: {reportFile}");
            break;
        }
        case "create":
        {
            var mode = args.ElementAtOrDefault(1) ?? "Unsafe";
            var created = await Create(mode, ParseInt(2, 12, 2, 32), ParseInt(3, 60, 10, 200));
            Console.WriteLine($"Experiment ID: {created.Id}");
            Console.WriteLine("Run 'join <id> <count> <client-label>' from two terminals, then 'report <id>'.");
            break;
        }
        case "join":
            await Send(ParseGuid(1), ParseInt(2, 6, 1, 32), args.ElementAtOrDefault(3) ?? Environment.MachineName);
            Console.WriteLine("Requests completed. Run 'report <id>' for the shared counter.");
            break;
        case "report":
            Print(await Get(ParseGuid(1)));
            break;
        case "clock":
        {
            var clientSkew = ParseInt(1, 90_000, -300_000, 300_000);
            var serverSkew = ParseInt(2, -45_000, -300_000, 300_000);
            var samples = new List<ClockReport>();
            for (var i = 0; i < 5; i++)
            {
                var t0 = DateTimeOffset.UtcNow.AddMilliseconds(clientSkew);
                var server = await http.GetFromJsonAsync<TimeSample>(
                    $"/api/concurrency-lab/time?serverSkewMs={serverSkew}&processingMs=20")
                    ?? throw new InvalidOperationException("No server timestamps.");
                var t3 = DateTimeOffset.UtcNow.AddMilliseconds(clientSkew);
                var offset = ((server.ServerReceivedAtUtc - t0).TotalMilliseconds +
                    (server.ServerSentAtUtc - t3).TotalMilliseconds) / 2;
                var rtt = (t3 - t0).TotalMilliseconds -
                    (server.ServerSentAtUtc - server.ServerReceivedAtUtc).TotalMilliseconds;
                samples.Add(new ClockReport(t0, server.ServerReceivedAtUtc, server.ServerSentAtUtc, t3, offset, rtt));
            }
            var best = samples.MinBy(x => x.NetworkRttMs)!;
            Console.WriteLine($"Client skew: {clientSkew:+0;-0;0} ms; server skew: {serverSkew:+0;-0;0} ms");
            Console.WriteLine($"Estimated server-client offset: {best.OffsetMs:+0.0;-0.0;0.0} ms");
            Console.WriteLine($"Estimated network RTT: {best.NetworkRttMs:0.0} ms");
            Console.WriteLine($"Corrected client timestamp: {best.ClientReceivedAtUtc.AddMilliseconds(best.OffsetMs):O}");
            var clockDirectory = Path.Combine(Directory.GetCurrentDirectory(), "lab-results");
            Directory.CreateDirectory(clockDirectory);
            var clockFile = Path.Combine(clockDirectory, $"clock-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
            await File.WriteAllTextAsync(clockFile, JsonSerializer.Serialize(new
            {
                recordedAtUtc = DateTimeOffset.UtcNow, server = baseUrl, clientSkewMs = clientSkew,
                serverSkewMs = serverSkew, selected = best, samples
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            Console.WriteLine($"JSON report: {clockFile}");
            break;
        }
        default: throw new ArgumentException("Use compare [count] [delayMs], create <mode> <count> <delayMs>, join <id> <count> <label>, report <id>, or clock [clientSkewMs] [serverSkewMs].");
    }
}
catch (Exception ex) when (ex is ArgumentException or HttpRequestException or InvalidOperationException or TaskCanceledException)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}

async Task<ExperimentSnapshot> Create(string mode, int count, int delay, bool startTogether = true)
{
    var modeValue = mode.ToLowerInvariant() switch
    {
        "unsafe" => 0, "lock" => 1, "semaphoreslim" or "semaphore" => 2,
        _ => throw new ArgumentException("Mode must be Unsafe, Lock, or SemaphoreSlim.")
    };
    using var response = await http.PostAsJsonAsync("/api/concurrency-lab/experiments",
        new { mode = modeValue, plannedRequests = count, workDelayMs = delay, startTogether });
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadFromJsonAsync<ExperimentSnapshot>()
        ?? throw new InvalidOperationException("Empty experiment response.");
}

async Task Send(Guid id, int count, string label)
{
    var requests = Enumerable.Range(1, count).Select(async number =>
    {
        using var response = await http.PostAsJsonAsync($"/api/concurrency-lab/experiments/{id}/reserve",
            new { clientId = $"{label}-{number:00}" });
        response.EnsureSuccessStatusCode();
    });
    await Task.WhenAll(requests);
}

async Task<ExperimentSnapshot> Get(Guid id) =>
    await http.GetFromJsonAsync<ExperimentSnapshot>($"/api/concurrency-lab/experiments/{id}")
    ?? throw new InvalidOperationException("Experiment not found.");

void Print(ExperimentSnapshot snapshot, long? elapsedMs = null, string? label = null)
{
    var mode = label ?? (snapshot.Mode switch { 0 => "Unsafe", 1 => "Lock", 2 => "SemaphoreSlim", _ => "Unknown" });
    Console.WriteLine($"{mode}: expected {snapshot.ExpectedReservations}, actual {snapshot.ActualReservations}, lost {snapshot.LostUpdates}; peak overlap {snapshot.MaxConcurrentRequests}; PID {snapshot.ProcessId}{(elapsedMs is null ? "" : $"; {elapsedMs} ms")}");
    foreach (var item in snapshot.Events)
        Console.WriteLine($"  {item.Sequence,3} {item.AtUtc:HH:mm:ss.fff} client={item.ClientId,-16} request={item.RequestNumber,2} {item.Phase,-7} value={item.Value?.ToString() ?? "-",2} thread={item.ThreadId}");
}

int ParseInt(int index, int fallback, int min, int max)
{
    var value = args.ElementAtOrDefault(index);
    if (value is null) return fallback;
    if (!int.TryParse(value, out var parsed) || parsed < min || parsed > max)
        throw new ArgumentException($"Argument {index} must be in {min}..{max}.");
    return parsed;
}

Guid ParseGuid(int index) => Guid.TryParse(args.ElementAtOrDefault(index), out var id)
    ? id : throw new ArgumentException($"Argument {index} must be an experiment GUID.");

internal sealed record RunReport(string Mode, long ElapsedMs, ExperimentSnapshot Snapshot);
internal sealed record ReservationEvent(long Sequence, int RequestNumber, string ClientId, string Phase,
    DateTimeOffset AtUtc, int ThreadId, int? Value);
internal sealed record ExperimentSnapshot(Guid Id, int Mode, bool StartTogether, int PlannedRequests, int WorkDelayMs,
    int AcceptedRequests, int CompletedRequests, int ExpectedReservations, int ActualReservations,
    int LostUpdates, int ActiveRequests, int MaxConcurrentRequests, int ProcessId,
    List<ReservationEvent> Events);
internal sealed record TimeSample(DateTimeOffset ServerReceivedAtUtc, DateTimeOffset ServerSentAtUtc, int ServerSkewMs);
internal sealed record ClockReport(DateTimeOffset ClientSentAtUtc, DateTimeOffset ServerReceivedAtUtc,
    DateTimeOffset ServerSentAtUtc, DateTimeOffset ClientReceivedAtUtc, double OffsetMs, double NetworkRttMs);
