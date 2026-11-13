using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Burmalda.ServiceDefaults;
using Microsoft.AspNetCore.DataProtection;

namespace Burmalda.DemoControl;

public sealed class DemoEngine : BackgroundService
{
    private readonly DemoStore store;
    private readonly DemoMetrics metrics;
    private readonly HttpClient http;
    private readonly IConfiguration configuration;
    private readonly IDataProtector protector;
    private readonly KubeControl kube;
    private readonly ILogger<DemoEngine> logger;
    private readonly SemaphoreSlim commands = new(1, 1);
    private readonly SemaphoreSlim anonymous = new(1, 1);
    private readonly ConcurrentDictionary<int, Task> actors = new();
    private readonly ConcurrentDictionary<string, DemoFault> faults = new();
    private readonly ConcurrentDictionary<string, string> pending = new();
    private readonly ConcurrentQueue<(DateTimeOffset At, double Ms, string Outcome)> window = new();
    private readonly ConcurrentDictionary<string, long> outcomes = new();
    private List<Fixture> fixtures = [];
    private readonly ConcurrentDictionary<int, SessionTokens> tokens = new();
    private CancellationTokenSource emergency = new();
    private volatile DemoRun? run;
    private volatile Preparation preparation = new(false, 0, 0, null);
    private volatile int target;
    private volatile int manualTarget = -1;
    private volatile bool stopping;
    private volatile bool aborting;
    private bool deletionDone, faultDone;
    private DateTimeOffset? manualRampAt;
    private int manualRampFrom;
    private string phase = "Ожидание запуска";
    private DateTimeOffset nextAnonymous;
    private long completedOperations, failedOperations;
    private Task preparationTask = Task.CompletedTask;

    public DemoEngine(DemoStore store, DemoMetrics metrics, IHttpClientFactory clients, IConfiguration configuration,
        IDataProtectionProvider protection, KubeControl kube, ILogger<DemoEngine> logger)
    {
        this.store = store; this.metrics = metrics; this.configuration = configuration; this.kube = kube; this.logger = logger;
        http = clients.CreateClient("traffic");
        protector = protection.CreateProtector("Burmalda.DemoControl.Fixtures.v1");
        metrics.Meter.CreateObservableGauge("burmalda_demo_clients", () => new[] {
            new Measurement<int>(target, new KeyValuePair<string, object?>("kind", "target")),
            new Measurement<int>(actors.Count(x => !x.Value.IsCompleted), new KeyValuePair<string, object?>("kind", "active")) });
        metrics.Meter.CreateObservableGauge("burmalda_demo_pending_operations", () => pending.Count);
        metrics.Meter.CreateObservableGauge("burmalda_demo_fault_active", () => new[] { "catalog", "gateway" }
            .Select(x => new Measurement<int>(GetFault(x).IsActive(DateTimeOffset.UtcNow) ? 1 : 0,
                new KeyValuePair<string, object?>("service", x))));
    }

    public EngineSnapshot Snapshot()
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-30);
        while (window.TryPeek(out var first) && first.At < cutoff) window.TryDequeue(out _);
        var samples = window.ToArray();
        var duration = Math.Clamp(run is null ? 30 : (DateTimeOffset.UtcNow - run.StartedAt).TotalSeconds, 1, 30);
        var latencies = samples.Select(x => x.Ms).Order().ToArray();
        var successes = samples.Count(x => x.Outcome == "success");
        return new(run, target, actors.Count(x => !x.Value.IsCompleted), phase, preparation,
            samples.Length / duration, successes / duration,
            latencies.Length == 0 ? 0 : latencies[(int)Math.Floor((latencies.Length - 1) * .95)],
            samples.Length == 0 ? 0 : 100.0 * (samples.Length - successes) / samples.Length,
            Interlocked.Read(ref completedOperations), Interlocked.Read(ref failedOperations), pending.Count,
            pending.Values.Order().ToArray(), new Dictionary<string, long>(outcomes),
            faults.Values.Where(x => x.IsActive(DateTimeOffset.UtcNow)).ToArray());
    }

    public DemoFault GetFault(string service) => faults.TryGetValue(service, out var fault) ? fault : new(service, 0, 0, DateTimeOffset.MinValue);

    public async Task SetFault(FaultRequest request)
    {
        request.Validate();
        faults[request.Service] = new(request.Service, request.ErrorPercent, request.DelayMs, DateTimeOffset.UtcNow.AddSeconds(request.DurationSeconds));
        await store.Event("fault_start", $"{request.Service}: {request.ErrorPercent}% ответов 503, задержка {request.DelayMs} мс, на {request.DurationSeconds} с. Сбой затрагивает всех клиентов API.");
    }
    public async Task ClearFaults()
    {
        faults.Clear();
        await store.Event("fault_clear", "Искусственные сбои сняты; экземпляры получат изменение в течение 2 секунд.");
    }

    public async Task Prepare(int count)
    {
        if (count is < 1 or > 500) throw new DemoConflict("Количество клиентов должно быть от 1 до 500.");
        await commands.WaitAsync();
        try
        {
            if (IsRunning || preparation.Running) throw new DemoConflict("Сначала завершите текущий запуск или подготовку.");
            preparation = new(true, 0, count, null);
            preparationTask = PrepareCore(count, StoppingToken);
        }
        finally { commands.Release(); }
        await store.Event("prepare", $"Подготовка {count} тестовых клиентов. Анонимные запросы ограничены 90/мин; аккаунты используются повторно.");
    }
    private CancellationToken StoppingToken { get; set; }
    private bool IsRunning => run?.State is "running" or "draining";

    private async Task PrepareCore(int count, CancellationToken ct)
    {
        try
        {
            var prefix = await store.Read<string>("prefix");
            if (prefix is null) { prefix = Guid.NewGuid().ToString("N")[..10]; await store.Write("prefix", prefix); }
            var password = configuration["Demo:FixturePassword"] ?? throw new DemoConflict("Не задан Demo:FixturePassword.");
            for (var index = 0; index < count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var existing = fixtures.FirstOrDefault(x => x.Index == index);
                var email = existing?.Email ?? $"demo-{prefix}-{index}@burmalda.example.test";
                var name = existing?.DisplayName ?? $"Demo {prefix} {index}";
                if (existing is null)
                {
                    using var registration = await Anonymous("/api/identity/register", new { email, displayName = name, password }, ct);
                    // A previous interrupted preparation can have registered this exact account already.
                    if (registration.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.Conflict))
                        throw new DemoConflict($"Регистрация клиента {index}: HTTP {(int)registration.StatusCode}.");
                }
                SessionTokens session;
                if (existing is not null)
                {
                    try { session = JsonSerializer.Deserialize<SessionTokens>(protector.Unprotect(existing.ProtectedTokens))!; }
                    catch (System.Security.Cryptography.CryptographicException) { session = await Login(email, password, ct); }
                    if (session.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(15))
                        session = await Login(email, password, ct);
                }
                else session = await Login(email, password, ct);
                tokens[index] = session;
                var bookId = existing?.BookId ?? Guid.Empty;
                var copyId = existing?.CopyId ?? Guid.Empty;
                if (index % 10 == 0 && bookId == Guid.Empty)
                {
                    // Stable ISBN makes a partially completed preparation recoverable through the public API.
                    var book = await Json("POST", "/api/catalog/books", session, new { isbn = $"DEMO-{prefix}-{index}",
                        title = $"Burmalda Demo {prefix} · книга {index / 10 + 1}", description = "Тестовые данные пульта нагрузки",
                        authors = new[] { "Burmalda Demo" }, genres = new[] { "Демонстрация" }, publisher = "Burmalda", publicationYear = 2026 }, "prepare", ct);
                    bookId = book.GetProperty("id").GetGuid();
                    var mine = await Json("GET", "/api/catalog/copies/mine", session, null, "prepare", ct);
                    var owned = mine.EnumerateArray().FirstOrDefault(x => x.GetProperty("bookId").GetGuid() == bookId);
                    if (owned.ValueKind != JsonValueKind.Undefined) copyId = owned.GetProperty("id").GetGuid();
                    else
                    {
                        var copy = await Json("POST", "/api/catalog/copies", session, new { bookId, condition = 1,
                            lendingPrice = 1m, maximumLoanDays = 14, location = "Minsk · Demo" }, "prepare", ct);
                        copyId = copy.GetProperty("id").GetGuid();
                    }
                }
                var fixture = new Fixture(index, email, name, protector.Protect(JsonSerializer.Serialize(session)), bookId, copyId);
                if (existing is not null) fixtures.Remove(existing);
                fixtures.Add(fixture);
                await store.Write("fixtures", fixtures.OrderBy(x => x.Index).ToArray());
                preparation = preparation with { Ready = index + 1 };
            }
            preparation = preparation with { Running = false };
            await store.Event("prepared", $"Готовы {count} клиентов. Созданы только собственные тестовые аккаунты и книги.");
        }
        catch (Exception ex)
        {
            preparation = preparation with { Running = false, Error = ex is OperationCanceledException ? "Подготовка прервана" : ex.Message };
            await store.Event("prepare_error", preparation.Error!);
            logger.LogWarning(ex, "Fixture preparation stopped");
        }
    }

    public async Task Start(RunOptions options)
    {
        options.Validate();
        await commands.WaitAsync();
        try
        {
            if (IsRunning || preparation.Running) throw new DemoConflict("Уже выполняется запуск или подготовка.");
            if (preparation.Ready < options.Peak || Enumerable.Range(0, options.Peak).Any(i => !tokens.TryGetValue(i, out var value) ||
                value.ExpiresAt < DateTimeOffset.UtcNow.AddSeconds(options.DurationSeconds + 120)))
                throw new DemoConflict("Сначала подготовьте нужное количество клиентов: токены должны действовать весь сценарий.");
            if (!pending.IsEmpty) throw new DemoConflict("Остались незавершённые бизнес-операции. Проверьте журнал и завершите их перед следующим запуском.");
            emergency.Dispose(); emergency = CancellationTokenSource.CreateLinkedTokenSource(StoppingToken);
            target = 0; manualTarget = -1; stopping = false; aborting = false; deletionDone = false; faultDone = false;
            manualRampAt = null;
            completedOperations = 0; failedOperations = 0; outcomes.Clear(); window.Clear();
            run = new(Guid.NewGuid(), options, DateTimeOffset.UtcNow);
            await store.Write("run", run);
            await store.Write("settings", options);
            await store.Event("run_start", $"Запуск {options.Scenario}, пик {options.Peak}, длительность {options.DurationSeconds} с.");
        }
        finally { commands.Release(); }
    }

    public async Task SetTarget(int clients)
    {
        await commands.WaitAsync();
        try
        {
            if (!IsRunning || stopping) throw new DemoConflict("Нет активного запуска.");
            if (clients < 0 || clients > Math.Min(500, preparation.Ready)) throw new DemoConflict("Сначала подготовьте это количество клиентов.");
            if (Enumerable.Range(0, clients).Any(i => !tokens.TryGetValue(i, out var session) || session.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(10)))
                throw new DemoConflict("Токены дополнительных клиентов истекают. Завершите запуск и повторите подготовку.");
            manualRampAt = null;
            manualTarget = clients;
            await store.Event("manual_target", $"Задано {clients} клиентов. Этапы нагрузки переведены в ручной режим.");
        }
        finally { commands.Release(); }
    }
    public async Task RampDown()
    {
        await commands.WaitAsync();
        try
        {
            if (!IsRunning || stopping) return;
            manualTarget = -1;
            manualRampFrom = target;
            manualRampAt = DateTimeOffset.UtcNow;
            deletionDone = true; faultDone = true;
            await store.Write("run", run);
            await store.Event("ramp_down", "Плавное снижение клиентов до нуля за 30 секунд, затем наблюдение HPA.");
        }
        finally { commands.Release(); }
    }
    public async Task StopRun(bool immediate)
    {
        await commands.WaitAsync();
        try
        {
            await ClearFaults();
            if (!IsRunning) return;
            if (stopping && (!immediate || aborting)) return;
            stopping = true; aborting |= immediate; target = 0;
            if (immediate) emergency.Cancel();
            run = run! with { State = "draining" };
            await store.Write("run", run);
            await store.Event(immediate ? "emergency_stop" : "drain", immediate ? "Аварийная остановка. Неизвестные результаты операций сохранены." :
                "Новые действия остановлены. Ожидание завершения начатых бизнес-операций.");
        }
        finally { commands.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StoppingToken = stoppingToken;
        fixtures = (await store.Read<Fixture[]>("fixtures") ?? []).OrderBy(x => x.Index).ToList();
        run = await store.Read<DemoRun>("run");
        foreach (var item in await store.Read<Dictionary<string, string>>("pending") ?? []) pending[item.Key] = item.Value;
        // Tokens are encrypted on disk and never included in snapshots or API responses.
        foreach (var fixture in fixtures)
        {
            try { tokens[fixture.Index] = JsonSerializer.Deserialize<SessionTokens>(protector.Unprotect(fixture.ProtectedTokens))!; }
            catch (System.Security.Cryptography.CryptographicException) { /* preparation will require a new login */ }
        }
        preparation = new(false, fixtures.Count, fixtures.Count, null);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await commands.WaitAsync(stoppingToken);
            try { await Tick(); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Demo scheduler tick failed");
                await store.Event("scheduler_error", ex.Message);
            }
            finally { commands.Release(); }
        }
    }

    private async Task Tick()
    {
        foreach (var fault in faults.Values.Where(x => !x.IsActive(DateTimeOffset.UtcNow)))
            if (faults.TryRemove(fault.Service, out _)) await store.Event("fault_expired", $"{fault.Service}: время сбоя истекло, ответы восстановлены.");
        foreach (var item in actors.Where(x => x.Value.IsCompleted)) actors.TryRemove(item.Key, out _);
        if (!IsRunning) return;
        var current = run!;
        var elapsed = (DateTimeOffset.UtcNow - current.StartedAt).TotalSeconds;
        target = stopping ? 0 : manualRampAt is { } rampAt ? (int)Math.Round(manualRampFrom * Math.Clamp(1 - (DateTimeOffset.UtcNow - rampAt).TotalSeconds / 30, 0, 1)) :
            manualTarget >= 0 ? manualTarget : ScenarioSchedule.Target(current.Options, elapsed);
        var nextPhase = stopping ? "Завершение операций" : manualRampAt is not null ? target > 0 ? "Клиенты уходят" : "Наблюдение снижения реплик" :
            manualTarget >= 0 ? "Ручная нагрузка" : ScenarioSchedule.Phase(current.Options, elapsed);
        if (phase != nextPhase) { phase = nextPhase; await store.Event("phase", phase); }
        for (var index = 0; index < target; index++)
            if (!actors.ContainsKey(index)) { var actorIndex = index; actors[index] = Task.Run(() => Actor(actorIndex, emergency.Token)); }
        if (!stopping && current.Options.Scenario == "recovery")
        {
            var killAt = 30 + current.Options.RampSeconds + current.Options.HoldSeconds / 2.0;
            if (!deletionDone && elapsed >= killAt)
            {
                deletionDone = true;
                var pods = await kube.Pods();
                var candidates = pods.Where(x => x.App == "catalog" && x.Ready && x.DeletingAt is null).ToArray();
                if (candidates.Length >= 2) await kube.DeletePod(candidates[0].Name, candidates[0].Uid);
                else await store.Event("pod_delete_skipped", "Удаление Catalog пропущено: нужно минимум два Ready Pod. Проверьте HPA и нагрузку.");
            }
            if (!faultDone && elapsed >= killAt + 30)
            { faultDone = true; await SetFault(new("catalog", 25, 0, 30)); }
        }
        if ((stopping || (manualRampAt is { } rampStart ? DateTimeOffset.UtcNow >= rampStart.AddSeconds(180) : elapsed >= current.Options.DurationSeconds)) && actors.IsEmpty)
        {
            await ClearFaults();
            run = current with { State = aborting ? "aborted" : pending.IsEmpty ? "completed" : "needs_review", FinishedAt = DateTimeOffset.UtcNow };
            await store.Write("run", run);
            await store.Write("result:" + current.Id, new { Run = run, Snapshot = Snapshot() });
            phase = pending.IsEmpty ? "Запуск завершён" : "Нужна проверка незавершённых операций";
            await store.Event("run_end", $"{phase}. Операций завершено: {completedOperations}, ошибок операций: {failedOperations}.");
        }
    }

    private async Task Actor(int index, CancellationToken ct)
    {
        try
        {
            await Task.Delay(Random.Shared.Next(50, 1500), ct);
            var fixture = fixtures.Single(x => x.Index == index / 10 * 10);
            var step = 0;
            while (!ct.IsCancellationRequested && !stopping && IsRunning && index < target)
            {
                try
                {
                    var session = tokens[index];
                    if (run!.Options.Scenario == "mixed" && index % 10 == 1 && step % 12 == 3)
                        await LoanCycle(index, fixture, ct);
                    else
                    {
                        var route = (step % 3) switch {
                            0 => "/api/catalog/books/search?q=Burmalda%20Demo&take=100",
                            1 => $"/api/catalog/books/{fixture.BookId}",
                            _ => $"/api/catalog/books/{fixture.BookId}/copies" };
                        using var response = await Send("GET", route, session, null, step % 3 == 0 ? "search" : "browse", ct);
                        await response.Content.LoadIntoBufferAsync(ct);
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested) { logger.LogDebug(ex, "Virtual client request failed"); }
                step++;
                await Task.Delay(Random.Shared.Next(1000, 3001), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task LoanCycle(int index, Fixture owner, CancellationToken ct)
    {
        var key = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (pending.ContainsKey(key)) return; // Never reuse a copy with an unknown previous outcome.
        pending[key] = $"Клиент {index}, экземпляр {owner.CopyId}: отправка заявки";
        await SavePending();
        try
        {
            var borrower = tokens[index]; var lender = tokens[owner.Index];
            var receipt = await Json("POST", "/api/loans/requests", borrower, new { copyId = owner.CopyId }, "loan_request", ct);
            var sagaId = receipt.GetProperty("id").GetGuid();
            pending[key] = $"Клиент {index}, saga {sagaId}: ожидание выдачи"; await SavePending();
            Guid loanId = Guid.Empty;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var saga = await Json("GET", $"/api/loans/requests/{sagaId}", borrower, null, "saga", ct);
                var state = saga.GetProperty("state").GetInt32();
                if (state == 2) { loanId = saga.GetProperty("loanId").GetGuid(); break; }
                if (state > 2) throw new DemoConflict($"Saga {sagaId} завершилась состоянием {state}.");
                await Task.Delay(2000, ct);
            }
            if (loanId == Guid.Empty) throw new DemoConflict($"Неизвестный результат saga {sagaId}; повторная заявка не отправляется.");
            pending[key] = $"Клиент {index}, выдача {loanId}: принятие / передача / возврат"; await SavePending();
            var accepted = await Json("POST", $"/api/loans/{loanId}/accept", lender, new { }, "accept", ct);
            await Json("POST", $"/api/loans/{loanId}/handover/confirm", borrower, new { code = accepted.GetProperty("handoverCode").GetString() }, "handover", ct);
            await Json("POST", $"/api/loans/{loanId}/return", borrower, new { }, "return", ct);
            await Json("POST", $"/api/loans/{loanId}/return/confirm", lender, new { }, "return_confirm", ct);
            // Wait for the real event-driven projection before reusing the same physical copy.
            deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            var available = false;
            while (DateTimeOffset.UtcNow < deadline)
            {
                var copies = await Json("GET", $"/api/catalog/books/{owner.BookId}/copies", borrower, null, "projection", ct);
                if (copies.EnumerateArray().Any(x => x.GetProperty("id").GetGuid() == owner.CopyId)) { available = true; break; }
                await Task.Delay(2000, ct);
            }
            if (!available) throw new DemoConflict($"Выдача {loanId} возвращена, но проекция экземпляра ещё не восстановлена.");
            pending.TryRemove(key, out _); await SavePending();
            Interlocked.Increment(ref completedOperations);
            metrics.Operations.Add(1, new KeyValuePair<string, object?>("outcome", "success"));
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref failedOperations);
            metrics.Operations.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
            pending[key] += " · " + (ct.IsCancellationRequested ? "остановлено, исход проверить" : ex.Message);
            await SavePending();
            await store.Event("operation_failed", pending[key]);
        }
    }
    private Task SavePending() => store.Write("pending", new Dictionary<string, string>(pending));

    public async Task Reconcile()
    {
        await commands.WaitAsync();
        try
        {
            if (IsRunning || preparation.Running) throw new DemoConflict("Завершите запуск и подготовку перед проверкой операций.");
            foreach (var key in pending.Keys)
            {
                try
                {
                    var index = int.Parse(key, System.Globalization.CultureInfo.InvariantCulture);
                    var owner = fixtures.Single(x => x.Index == index / 10 * 10);
                    var borrower = tokens[index]; var lender = tokens[owner.Index];
                    if (borrower.ExpiresAt < DateTimeOffset.UtcNow || lender.ExpiresAt < DateTimeOffset.UtcNow)
                        throw new DemoConflict("Повторите подготовку клиентов для обновления токенов.");
                    var loans = await Json("GET", "/api/loans/mine", borrower, null, "reconcile", StoppingToken);
                    var loan = loans.EnumerateArray().Where(x => x.GetProperty("copyId").GetGuid() == owner.CopyId)
                        .OrderByDescending(x => x.GetProperty("createdAt").GetDateTimeOffset()).FirstOrDefault();
                    if (loan.ValueKind != JsonValueKind.Undefined)
                    {
                        var id = loan.GetProperty("id").GetGuid(); var status = loan.GetProperty("status").GetInt32();
                        // Read first, then only the transition valid for that exact state. Never recreate a request.
                        if (status is 0 or 1) await Json("POST", $"/api/loans/{id}/cancel", borrower, new { }, "reconcile", StoppingToken);
                        else if (status is 5 or 8)
                        {
                            await Json("POST", $"/api/loans/{id}/return", borrower, new { }, "reconcile", StoppingToken);
                            await Json("POST", $"/api/loans/{id}/return/confirm", lender, new { }, "reconcile", StoppingToken);
                        }
                        else if (status == 6) await Json("POST", $"/api/loans/{id}/return/confirm", lender, new { }, "reconcile", StoppingToken);
                        else if (status is not (2 or 3 or 7)) throw new DemoConflict($"Выдача {id}, состояние {status}: нужна ручная проверка.");
                    }
                    // An unacknowledged request may still have a live saga; wait beyond its 2-minute lease.
                    if (DateTimeOffset.UtcNow - (run?.FinishedAt ?? DateTimeOffset.UtcNow) < TimeSpan.FromMinutes(3))
                        throw new DemoConflict("Переходы проверены. Повторите проверку через 3 минуты после остановки, чтобы исключить незавершённую saga.");
                    var copies = await Json("GET", $"/api/catalog/books/{owner.BookId}/copies", borrower, null, "reconcile", StoppingToken);
                    if (!copies.EnumerateArray().Any(x => x.GetProperty("id").GetGuid() == owner.CopyId))
                        throw new DemoConflict("Экземпляр ещё не доступен в проекции. Повторите проверку позже.");
                    pending.TryRemove(key, out _);
                    await store.Event("reconciled", $"Клиент {index}: текущее состояние проверено, экземпляр снова доступен.");
                }
                catch (Exception ex) { await store.Event("reconcile_pending", $"Клиент {key}: {ex.Message}"); }
            }
            await SavePending();
        }
        finally { commands.Release(); }
    }

    private async Task<SessionTokens> Login(string email, string password, CancellationToken ct)
    {
        using var response = await Anonymous("/api/identity/login", new { email, password }, ct);
        if (!response.IsSuccessStatusCode) throw new DemoConflict($"Вход тестового клиента: HTTP {(int)response.StatusCode}.");
        return (await response.Content.ReadFromJsonAsync<SessionTokens>(ct))!;
    }
    private async Task<HttpResponseMessage> Anonymous(string route, object body, CancellationToken ct)
    {
        await anonymous.WaitAsync(ct);
        try
        {
            var pause = nextAnonymous - DateTimeOffset.UtcNow;
            if (pause > TimeSpan.Zero) await Task.Delay(pause, ct);
            nextAnonymous = DateTimeOffset.UtcNow.AddMilliseconds(680);
            var response = await http.PostAsJsonAsync(route, body, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // Honor Retry-After but do not replay an uncertain registration/login automatically.
                var retry = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60);
                nextAnonymous = DateTimeOffset.UtcNow + retry;
            }
            return response;
        }
        finally { anonymous.Release(); }
    }
    private async Task<JsonElement> Json(string method, string route, SessionTokens session, object? body, string operation, CancellationToken ct)
    {
        using var response = await Send(method, route, session, body, operation, ct);
        if (!response.IsSuccessStatusCode) throw new DemoConflict($"{operation}: HTTP {(int)response.StatusCode}; изменяющий запрос автоматически не повторяется.");
        if (response.StatusCode == HttpStatusCode.NoContent) return default;
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return document.RootElement.Clone();
    }
    private async Task<HttpResponseMessage> Send(string method, string route, SessionTokens session, object? body, string operation, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        if (body is not null) request.Content = JsonContent.Create(body);
        var started = Stopwatch.GetTimestamp();
        string outcome;
        try
        {
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            outcome = response.IsSuccessStatusCode ? "success" : (int)response.StatusCode switch {
                429 => "rate_limited", >= 500 => "server_error", 401 or 403 => "auth_error", _ => "client_error" };
            Record(); return response;
        }
        catch (Exception ex)
        {
            outcome = ex is OperationCanceledException ? ct.IsCancellationRequested ? "cancelled" : "timeout" : "network_error";
            Record(); throw;
        }
        void Record()
        {
            var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            metrics.Requests.Add(1, new("operation", operation), new("outcome", outcome));
            metrics.Duration.Record(seconds, new KeyValuePair<string, object?>("operation", operation));
            outcomes.AddOrUpdate(outcome, 1, (_, count) => count + 1);
            window.Enqueue((DateTimeOffset.UtcNow, seconds * 1000, outcome));
            while (window.TryPeek(out var first) && first.At < DateTimeOffset.UtcNow.AddSeconds(-30)) window.TryDequeue(out _);
        }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        target = 0; stopping = true; emergency.Cancel(); faults.Clear();
        await base.StopAsync(cancellationToken);
        await Task.WhenAll(actors.Values.Append(preparationTask)).WaitAsync(cancellationToken);
        if (run?.State is "running" or "draining")
        {
            run = run with { State = "interrupted", FinishedAt = DateTimeOffset.UtcNow };
            await store.Write("run", run);
            await store.Write("result:" + run.Id, new { Run = run, Snapshot = Snapshot() });
        }
    }
}
