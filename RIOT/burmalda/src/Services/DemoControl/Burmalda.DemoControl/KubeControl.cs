using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Burmalda.DemoControl;

public sealed class KubeControl : IDisposable
{
    private readonly HttpClient client;
    private readonly string ns;
    private readonly string tokenFile;
    private readonly DemoStore store;
    private readonly bool available;
    private readonly SemaphoreSlim deletion = new(1, 1);
    public KubeControl(IConfiguration configuration, DemoStore store)
    {
        this.store = store;
        ns = configuration["POD_NAMESPACE"] ?? "burmalda";
        var directory = configuration["Demo:ServiceAccountDirectory"] ?? "/var/run/secrets/kubernetes.io/serviceaccount";
        tokenFile = Path.Combine(directory, "token");
        available = File.Exists(tokenFile);
        var handler = new HttpClientHandler();
        if (available)
        {
            var root = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(directory, "ca.crt")));
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is null || (errors & System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(root);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(certificate);
            };
        }
        client = new(handler) { BaseAddress = new Uri("https://kubernetes.default.svc"), Timeout = TimeSpan.FromSeconds(5) };
    }
    private async Task<JsonElement> Get(string resource)
    {
        if (!available) throw new DemoConflict("Kubernetes API недоступен: сервис запущен вне кластера.");
        using var request = new HttpRequestMessage(HttpMethod.Get, resource);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await File.ReadAllTextAsync(tokenFile)).Trim());
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return doc.RootElement.Clone();
    }
    public async Task<PodView[]> Pods()
    {
        var result = await Get($"/api/v1/namespaces/{ns}/pods?labelSelector=app.kubernetes.io%2Fpart-of%3Dburmalda");
        return result.GetProperty("items").EnumerateArray().Select(pod =>
        {
            var meta = pod.GetProperty("metadata"); var status = pod.GetProperty("status");
            var ready = status.TryGetProperty("conditions", out var conditions) && conditions.EnumerateArray().Any(c =>
                c.GetProperty("type").GetString() == "Ready" && c.GetProperty("status").GetString() == "True");
            var restarts = status.TryGetProperty("containerStatuses", out var containers) ? containers.EnumerateArray().Sum(x => x.GetProperty("restartCount").GetInt32()) : 0;
            var app = meta.GetProperty("labels").TryGetProperty("app", out var label) ? label.GetString()! : "unknown";
            return new PodView(meta.GetProperty("name").GetString()!, meta.GetProperty("uid").GetString()!, app,
                status.GetProperty("phase").GetString()!, ready, restarts,
                meta.TryGetProperty("deletionTimestamp", out var deleting) ? deleting.GetDateTimeOffset() : null);
        }).OrderBy(x => x.App).ThenBy(x => x.Name).ToArray();
    }
    public async Task<HpaView[]> Hpas()
    {
        var result = await Get($"/apis/autoscaling/v2/namespaces/{ns}/horizontalpodautoscalers");
        return result.GetProperty("items").EnumerateArray().Select(item =>
        {
            var spec = item.GetProperty("spec"); var status = item.GetProperty("status");
            double? cpu = null;
            if (status.TryGetProperty("currentMetrics", out var metrics))
                foreach (var metric in metrics.EnumerateArray())
                    if (metric.TryGetProperty("resource", out var resource) && resource.GetProperty("name").GetString() == "cpu" &&
                        resource.GetProperty("current").TryGetProperty("averageUtilization", out var utilization)) cpu = utilization.GetDouble();
            return new HpaView(item.GetProperty("metadata").GetProperty("name").GetString()!,
                status.TryGetProperty("currentReplicas", out var current) ? current.GetInt32() : 0,
                status.TryGetProperty("desiredReplicas", out var desired) ? desired.GetInt32() : 0,
                spec.GetProperty("minReplicas").GetInt32(), spec.GetProperty("maxReplicas").GetInt32(), cpu,
                status.TryGetProperty("conditions", out var conditions) ? string.Join(" · ", conditions.EnumerateArray().Select(c =>
                    c.GetProperty("type").GetString() + "=" + c.GetProperty("status").GetString() + ": " + c.GetProperty("reason").GetString())) : "Ожидание метрик");
        }).ToArray();
    }
    public async Task DeletePod(string name, string uid)
    {
        await deletion.WaitAsync();
        try { await DeletePodCore(name, uid); }
        finally { deletion.Release(); }
    }
    private async Task DeletePodCore(string name, string uid)
    {
        // Verify live owner chain and UID, rather than trusting an app label supplied by the caller.
        var pod = await Get($"/api/v1/namespaces/{ns}/pods/{Uri.EscapeDataString(name)}");
        var meta = pod.GetProperty("metadata");
        if (meta.GetProperty("uid").GetString() != uid || meta.TryGetProperty("deletionTimestamp", out _))
            throw new DemoConflict("Pod уже изменился или удаляется. Обновите состояние.");
        var rsOwner = meta.GetProperty("ownerReferences").EnumerateArray().SingleOrDefault(x => x.GetProperty("kind").GetString() == "ReplicaSet");
        if (rsOwner.ValueKind == JsonValueKind.Undefined) throw new DemoConflict("Разрешены только Pod Deployment Catalog/Gateway.");
        var rs = await Get($"/apis/apps/v1/namespaces/{ns}/replicasets/{Uri.EscapeDataString(rsOwner.GetProperty("name").GetString()!)}");
        if (rs.GetProperty("metadata").GetProperty("uid").GetString() != rsOwner.GetProperty("uid").GetString()) throw new DemoConflict("Владелец Pod изменился.");
        var owner = rs.GetProperty("metadata").GetProperty("ownerReferences").EnumerateArray().SingleOrDefault(x => x.GetProperty("kind").GetString() == "Deployment");
        var app = owner.ValueKind == JsonValueKind.Undefined ? "" : owner.GetProperty("name").GetString();
        if (app is not ("catalog" or "gateway")) throw new DemoConflict("Разрешены только Catalog и Gateway.");
        var deployment = await Get($"/apis/apps/v1/namespaces/{ns}/deployments/{app}");
        if (deployment.GetProperty("metadata").GetProperty("uid").GetString() != owner.GetProperty("uid").GetString())
            throw new DemoConflict("Deployment изменился.");
        if ((await Pods()).Count(x => x.App == app && x.Ready && x.DeletingAt is null) < 2)
            throw new DemoConflict("Для демонстрации восстановления нужны минимум два Ready Pod этого сервиса.");
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/namespaces/{ns}/pods/{Uri.EscapeDataString(name)}") {
            Content = JsonContent.Create(new { apiVersion = "v1", kind = "DeleteOptions", gracePeriodSeconds = 5, preconditions = new { uid } }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await File.ReadAllTextAsync(tokenFile)).Trim());
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        await store.Event("pod_deleted", $"Удалён {app} Pod {name} · UID {uid}. Замена создаётся контроллером Kubernetes.");
    }
    public void Dispose() => client.Dispose();
}

public sealed class ClusterObserver(KubeControl kube, IHttpClientFactory clients, DemoStore store,
    ILogger<ClusterObserver> logger) : BackgroundService
{
    private volatile ClusterSnapshot snapshot = new(null, "Ожидание первого измерения", [], [], [], null);
    public ClusterSnapshot Snapshot => snapshot;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (!stoppingToken.IsCancellationRequested)
        {
            var old = snapshot;
            var pods = old.Pods; var hpas = old.Hpas; string? error = null;
            DateTimeOffset? checkedAt = old.CheckedAt, metricsAt = old.MetricsAt;
            var charts = old.Charts;
            try
            {
                pods = await kube.Pods(); hpas = await kube.Hpas(); checkedAt = DateTimeOffset.UtcNow;
                foreach (var pod in pods.Where(p => old.CheckedAt is not null && !old.Pods.Any(x => x.Uid == p.Uid)))
                    await store.Event("pod_created", $"Новый Pod {pod.Name} · {pod.Phase}, Ready={pod.Ready}.");
                foreach (var pod in pods.Where(p => p.Ready && old.Pods.Any(x => x.Uid == p.Uid && !x.Ready)))
                    await store.Event("pod_ready", $"{pod.Name} перешёл в Ready и принимает трафик.");
                foreach (var pod in old.Pods.Where(p => !pods.Any(x => x.Uid == p.Uid)))
                    await store.Event("pod_gone", $"Pod {pod.Name} исчез из Kubernetes API.");
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { error = "Kubernetes: " + ex.Message; }
            try
            {
                var queries = new Dictionary<string, string> {
                    ["rps"] = "sum(rate(burmalda_demo_requests_total{operation!=\"prepare\"}[30s]))",
                    ["success"] = "sum(rate(burmalda_demo_requests_total{operation!=\"prepare\",outcome=\"success\"}[30s]))",
                    ["p95"] = "histogram_quantile(0.95,sum by(le)(rate(burmalda_demo_request_duration_seconds_bucket[30s]))) * 1000",
                    ["clients"] = "sum(burmalda_demo_clients{kind=\"active\"})" };
                var series = await Task.WhenAll(queries.Select(async entry =>
                {
                    var end = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var route = $"/api/v1/query_range?query={Uri.EscapeDataString(entry.Value)}&start={end - 900}&end={end}&step=5";
                    using var response = await clients.CreateClient("prometheus").GetAsync(route, stoppingToken);
                    response.EnsureSuccessStatusCode();
                    using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(stoppingToken), cancellationToken: stoppingToken);
                    var result = doc.RootElement.GetProperty("data").GetProperty("result");
                    var points = result.GetArrayLength() == 0 ? [] : result[0].GetProperty("values").EnumerateArray()
                        .Where(p => double.TryParse(p[1].GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
                        .Select(p => new ChartPoint(p[0].GetDouble(), double.Parse(p[1].GetString()!, System.Globalization.CultureInfo.InvariantCulture))).ToArray();
                    return new KeyValuePair<string, ChartPoint[]>(entry.Key, points);
                }));
                charts = series.ToDictionary();
                // Scrape timestamp, rather than query wall-clock time, distinguishes a dead exporter from idle traffic.
                using var fresh = await clients.CreateClient("prometheus").GetAsync("/api/v1/query?query=" + Uri.EscapeDataString("max(timestamp(burmalda_demo_clients))"), stoppingToken);
                fresh.EnsureSuccessStatusCode();
                using var freshDoc = await JsonDocument.ParseAsync(await fresh.Content.ReadAsStreamAsync(stoppingToken), cancellationToken: stoppingToken);
                var freshResult = freshDoc.RootElement.GetProperty("data").GetProperty("result");
                if (freshResult.GetArrayLength() > 0 && double.TryParse(freshResult[0].GetProperty("value")[1].GetString(),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var stamp))
                    metricsAt = DateTimeOffset.FromUnixTimeSeconds((long)stamp);
                if (metricsAt is null || DateTimeOffset.UtcNow - metricsAt > TimeSpan.FromSeconds(30))
                    error = (error is null ? "" : error + " · ") + "Метрики ещё не поступили или устарели";
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            { error = (error is null ? "" : error + " · ") + "Prometheus: " + ex.Message; logger.LogDebug(ex, "Prometheus unavailable"); }
            snapshot = new(checkedAt, error, pods, hpas, charts, metricsAt);
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
