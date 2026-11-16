using System.Collections.Concurrent;
using System.Net.Http.Json;

namespace Burmalda.DemoControl;

public sealed record ReleaseStage(string Name, string Status, long StartedAtMs, long DurationMs);
public sealed record ReleaseCommand(bool Pending = false, string? Command = null, string? Error = null, string? Commit = null);
public sealed record ReleaseBuild(int Number, bool Building, string? Result, string Commit, long StartedAtMs,
    long DurationMs, long CheckedAtMs, ReleaseCommand Command, ReleaseStage[] Stages);
public sealed record TrafficProof(DateTimeOffset? From, DateTimeOffset? To, long Requests, long Errors,
    double P95Ms, Dictionary<string, long> Versions, Dictionary<string, long> Outcomes, bool Covered);
public sealed record ReleaseView(bool Enabled, DateTimeOffset? CheckedAt, string? Error, ReleaseBuild? Build, TrafficProof Traffic);

// Bounded, real responses from the existing HTTP load generator; no retries are hidden.
public sealed class ReleaseTraffic
{
    private readonly object gate = new();
    private readonly Queue<(DateTimeOffset At, double Ms, string Outcome, string Version)> samples = new();
    private DateTimeOffset since = DateTimeOffset.UtcNow;
    public void Record(DateTimeOffset at, double ms, string outcome, string version)
    {
        lock (gate)
        {
            samples.Enqueue((at, ms, outcome, version));
            while (samples.Count > 200_000 || samples.TryPeek(out var first) && first.At < at.AddHours(-1))
                since = samples.Dequeue().At;
        }
    }
    public TrafficProof Read(DateTimeOffset? from, DateTimeOffset? to)
    {
        lock (gate)
        {
            if (from is null) return new(null, null, 0, 0, 0, [], [], false);
            var end = to ?? DateTimeOffset.UtcNow;
            var rows = samples.Where(x => x.At >= from && x.At <= end).ToArray();
            var latency = rows.Select(x => x.Ms).Order().ToArray();
            return new(from, end, rows.LongLength, rows.LongCount(x => x.Outcome != "success"),
                latency.Length == 0 ? 0 : latency[(int)Math.Floor((latency.Length - 1) * .95)],
                rows.Where(x => x.Version.Length > 0).GroupBy(x => x.Version).ToDictionary(x => x.Key, x => x.LongCount()),
                rows.GroupBy(x => x.Outcome).ToDictionary(x => x.Key, x => x.LongCount()), since <= from);
        }
    }
}

public sealed class ReleaseObserver(IHttpClientFactory clients, IConfiguration configuration, ReleaseTraffic traffic,
    DemoStore store) : BackgroundService
{
    public bool Enabled => configuration.GetValue<bool>("Release:Enabled");
    private volatile ReleaseView snapshot = new(false, null, null, null, new(null, null, 0, 0, 0, [], [], false));
    private readonly SemaphoreSlim command = new(1, 1);
    public ReleaseView Snapshot => snapshot;

    private HttpClient Client()
    {
        var client = clients.CreateClient("release");
        client.DefaultRequestHeaders.Add("X-Release-Key", configuration["Release:Key"] ?? "");
        return client;
    }
    public async Task Publish()
    {
        if (!Enabled) throw new DemoConflict("Запустите стенд через tools/LiveRelease.ps1 Start.");
        if (!await command.WaitAsync(0)) throw new DemoConflict("Запрос выпуска уже отправляется.");
        try
        {
            using var client = Client();
            using var response = await client.PostAsJsonAsync("/release", new { });
            if (!response.IsSuccessStatusCode) throw new DemoConflict("Выпуск недоступен: дождитесь текущей сборки и проверьте локальный мост Jenkins.");
            snapshot = snapshot with { Build = snapshot.Build is { } build ? build with { Command = new(true, "publishing") } : null };
            await store.Event("release_request", "Запрошен выпуск: изменение C# в demo/live-release, Git push и Jenkins Pipeline.");
        }
        catch (HttpRequestException) { throw new DemoConflict("Нет связи с мостом Jenkins. Выполните tools/LiveRelease.ps1 Start."); }
        finally { command.Release(); }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        using var client = Client();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var build = await client.GetFromJsonAsync<ReleaseBuild>("/state", stoppingToken) ?? throw new HttpRequestException();
                var deploy = build.Stages.FirstOrDefault(x => x.Name == "Deploy");
                var from = deploy is null ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeMilliseconds(deploy.StartedAtMs);
                var to = build.Building ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeMilliseconds(build.StartedAtMs + build.DurationMs);
                var proof = traffic.Read(from, to);
                var previous = snapshot.Build;
                if (previous?.Number != build.Number)
                    await store.Event("release_started", $"Jenkins #{build.Number} · коммит {Short(build.Commit)} · {build.Result ?? "выполняется"}.");
                foreach (var stage in build.Stages.Where(x => previous?.Number != build.Number || previous.Stages.FirstOrDefault(p => p.Name == x.Name)?.Status != x.Status))
                    await store.Event("release_stage", $"Jenkins #{build.Number} · {stage.Name}: {stage.Status}.");
                snapshot = new(true, DateTimeOffset.UtcNow, build.Command.Error, build, proof);
                if (!build.Building && (previous?.Number != build.Number || previous.Building))
                {
                    await store.Write("release-result:" + build.Number, snapshot);
                    await store.Event("release_finished", $"Jenkins #{build.Number}: {build.Result}. Во время Deploy/Verify: {proof.Requests} запросов, {proof.Errors} ошибок.");
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            { snapshot = snapshot with { Enabled = true, Error = "Не удалось обновить данные Jenkins. Проверьте tools/LiveRelease.ps1 Status." }; }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
    public static string Short(string value) => value.Length > 12 ? value[..12] : value.Length == 0 ? "ожидается SHA" : value;
}
