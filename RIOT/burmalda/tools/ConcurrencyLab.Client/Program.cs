using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);

var baseUrl = Environment.GetEnvironmentVariable("BURMALDA_LAB_URL") ?? "http://localhost:5107";
using var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 32 })
{ BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
var action = args.ElementAtOrDefault(0)?.ToLowerInvariant() ?? "compare";

try
{
    switch (action)
    {
        case "db-create":
        {
            var mode = args.ElementAtOrDefault(1)?.ToLowerInvariant() switch
            { "unsafe" => 0, "lock" => 1, "none" => 2, "transaction" => 3, "saga" => 4,
                _ => throw new ArgumentException("Use db-create unsafe|lock|none|transaction|saga [failure: 0..4].") };
            using var response = await http.PostAsJsonAsync("/api/concurrency-lab/database/runs", new { mode, failure = ParseInt(2, 0, 0, 4) });
            PrintDatabase(await ReadDatabase(response));
            break;
        }
        case "db-reserve":
        case "db-execute":
        case "db-step":
        {
            var id = ParseGuid(1);
            var endpoint = action[3..];
            using var response = await http.PostAsJsonAsync($"/api/concurrency-lab/database/runs/{id}/{endpoint}",
                new { clientId = args.ElementAtOrDefault(2) ?? "Client 1", sentAtUtc = DateTimeOffset.UtcNow });
            PrintDatabase(await ReadDatabase(response));
            break;
        }
        case "db-report":
        {
            using var response = await http.GetAsync($"/api/concurrency-lab/database/runs/{ParseGuid(1)}");
            PrintDatabase(await ReadDatabase(response));
            break;
        }
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
            WriteLog("info", $"JSON report: {reportFile}");
            break;
        }
        case "create":
        {
            var mode = args.ElementAtOrDefault(1) ?? "Unsafe";
            var created = await Create(mode, ParseInt(2, 12, 2, 32), ParseInt(3, 60, 10, 200));
            WriteLog("info", $"Experiment ID: {created.Id}");
            WriteLog("info", "Run 'join <id> <count> <client-label>' from two terminals, then 'report <id>'.");
            break;
        }
        case "join":
            await Send(ParseGuid(1), ParseInt(2, 6, 1, 32), args.ElementAtOrDefault(3) ?? Environment.MachineName);
            WriteLog("info", "Requests completed. Run 'report <id>' for the shared counter.");
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
            WriteLog("info", $"Client skew: {clientSkew:+0;-0;0} ms; server skew: {serverSkew:+0;-0;0} ms");
            WriteLog("info", $"Estimated server-client offset: {best.OffsetMs:+0.0;-0.0;0.0} ms");
            WriteLog("info", $"Estimated network RTT: {best.NetworkRttMs:0.0} ms");
            WriteLog("info", $"Corrected client timestamp: {best.ClientReceivedAtUtc.AddMilliseconds(best.OffsetMs):O}");
            var clockDirectory = Path.Combine(Directory.GetCurrentDirectory(), "lab-results");
            Directory.CreateDirectory(clockDirectory);
            var clockFile = Path.Combine(clockDirectory, $"clock-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
            await File.WriteAllTextAsync(clockFile, JsonSerializer.Serialize(new
            {
                recordedAtUtc = DateTimeOffset.UtcNow, server = baseUrl, clientSkewMs = clientSkew,
                serverSkewMs = serverSkew, selected = best, samples
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            WriteLog("info", $"JSON report: {clockFile}");
            break;
        }
        default: throw new ArgumentException("Use compare [count] [delayMs], create <mode> <count> <delayMs>, join <id> <count> <label>, report <id>, or clock [clientSkewMs] [serverSkewMs].");
    }
}
catch (Exception ex) when (ex is ArgumentException or HttpRequestException or InvalidOperationException or TaskCanceledException)
{
    WriteLog("error", ex.Message);
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
    WriteLog(snapshot.LostUpdates > 0 ? "warn" : "info", $"{mode}: expected {snapshot.ExpectedReservations}, actual {snapshot.ActualReservations}, lost {snapshot.LostUpdates}; peak overlap {snapshot.MaxConcurrentRequests}; PID {snapshot.ProcessId}{(elapsedMs is null ? "" : $"; {elapsedMs} ms")}");
    if (args.Contains("--verbose"))
        foreach (var item in snapshot.Events)
            WriteLog("info", $"{item.AtUtc:HH:mm:ss.fff} client={item.ClientId} request={item.RequestNumber} {item.Phase} value={item.Value?.ToString() ?? "-"} thread={item.ThreadId}");
}

async Task<JsonElement> ReadDatabase(HttpResponseMessage response)
{
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        string? message = null;
        try { using var json = JsonDocument.Parse(body); if (json.RootElement.TryGetProperty("message", out var value)) message = value.GetString(); } catch (JsonException) { }
        throw new HttpRequestException(message ?? $"HTTP {(int)response.StatusCode}");
    }
    using var document = JsonDocument.Parse(body);
    return document.RootElement.Clone();
}

void PrintDatabase(JsonElement snapshot)
{
    var directory = Path.Combine(Directory.GetCurrentDirectory(), "lab-results"); Directory.CreateDirectory(directory);
    var id = snapshot.GetProperty("id").GetGuid();
    var file = Path.Combine(directory, $"database-lab-{id}-{Guid.NewGuid().ToString()[..8]}.json");
    File.WriteAllText(file, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
    var state = snapshot.GetProperty("after");
    WriteLog("info", $"{id} · {snapshot.GetProperty("status")} · available={state.GetProperty("copyAvailable")} loans={state.GetProperty("loans")} points={state.GetProperty("reservedPoints")}");
    WriteLog("info", $"JSON: {file}");
    if (args.Contains("--verbose"))
        foreach (var evt in snapshot.GetProperty("events").EnumerateArray())
            WriteLog(evt.GetProperty("level").GetString()!, $"{evt.GetProperty("atUtc")} {evt.GetProperty("clientId")} → {evt.GetProperty("phase")} · {evt.GetProperty("detail")}");
}

void WriteLog(string level, string message)
{
    var tag = $"[{level}]";
    if (!Console.IsOutputRedirected) tag = $"\u001b[{(level == "error" ? "31" : level == "warn" ? "33" : "36")}m{tag}\u001b[0m";
    Console.WriteLine($"{tag} {message}");
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
