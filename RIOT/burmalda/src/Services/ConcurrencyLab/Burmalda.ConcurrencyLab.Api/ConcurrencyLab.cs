using System.Collections.Concurrent;
using System.Diagnostics;

namespace Burmalda.ConcurrencyLab.Api;

public enum ReservationMode { Unsafe, Lock, SemaphoreSlim }

public sealed record CreateExperimentRequest(
    ReservationMode Mode,
    int PlannedRequests, 
    int WorkDelayMs,
    bool StartTogether = true
    );
public sealed record ReserveRequest(string ClientId);
public sealed record ReservationEvent(long Sequence, 
    int RequestNumber, 
    string ClientId, 
    string Phase,
    DateTimeOffset AtUtc, 
    int ThreadId, 
    int? Value);
public sealed record ReservationResult(int RequestNumber, string ClientId, int ReadValue, int WrittenValue,
    int ThreadAtStart, int ThreadAtFinish, DateTimeOffset StartedAtUtc, DateTimeOffset FinishedAtUtc);
public sealed record ExperimentSnapshot(Guid Id, ReservationMode Mode, bool StartTogether, int PlannedRequests, int WorkDelayMs,
    int AcceptedRequests, int CompletedRequests, int ExpectedReservations, int ActualReservations,
    int LostUpdates, int ActiveRequests, int MaxConcurrentRequests, int ProcessId,
    IReadOnlyList<ReservationEvent> Events);
public sealed record TimeSample(DateTimeOffset ServerReceivedAtUtc, DateTimeOffset ServerSentAtUtc, int ServerSkewMs);

public static class ConcurrencyLabEndpoints
{
    public static IEndpointRouteBuilder MapConcurrencyLab(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/concurrency-lab");
        group.MapPost("/experiments", (CreateExperimentRequest request, ExperimentStore store) =>
        {
            if (!Enum.IsDefined(request.Mode) || request.PlannedRequests is < 2 or > 32 ||
                request.WorkDelayMs is < 10 or > 2000)
                return Results.BadRequest(new { message = "Use 2..32 requests and a 10..200 ms work delay." });

            var experiment = store.Create(request);
            return Results.Created($"/api/concurrency-lab/experiments/{experiment.Id}", experiment.Snapshot());
        });
        group.MapGet("/experiments/{id:guid}", (Guid id, ExperimentStore store) =>
            store.Get(id) is { } experiment ? Results.Ok(experiment.Snapshot()) : Results.NotFound());
        group.MapPost("/experiments/{id:guid}/reserve", async (Guid id, ReserveRequest request,
            ExperimentStore store, CancellationToken ct) =>
        {
            if (store.Get(id) is not { } experiment) 
                return Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.ClientId) || request.ClientId.Length > 32)
                return Results.BadRequest(new { message = "ClientId must have 1..32 characters." });
            if (!experiment.TryAccept(out var requestNumber))
                return Results.Conflict(new { message = "The experiment already accepted every planned request." });
            return Results.Ok(await experiment.ReserveAsync(requestNumber, request.ClientId, ct));
        });
        group.MapGet("/time", async (int serverSkewMs, int processingMs, CancellationToken ct) =>
        {
            if (serverSkewMs is < -300_000 or > 300_000 || processingMs is < 0 or > 500)
                return Results.BadRequest(new { message = "serverSkewMs must be within ±300000; processingMs within 0..500." });
            var skew = TimeSpan.FromMilliseconds(serverSkewMs);
            var received = DateTimeOffset.UtcNow + skew;
            if (processingMs > 0)
                await Task.Delay(processingMs, ct);
            return Results.Ok(new TimeSample(received, DateTimeOffset.UtcNow + skew, serverSkewMs));
        });
        return app;
    }
}

public sealed class ExperimentStore
{
    private readonly ConcurrentDictionary<Guid, ReservationExperiment> _experiments = new();

    public ReservationExperiment Create(CreateExperimentRequest request)
    {
        foreach (var (id, experiment) in _experiments)
            if (experiment.CreatedAtUtc < DateTimeOffset.UtcNow.AddMinutes(-30))
                _experiments.TryRemove(id, out _);
        var created = new ReservationExperiment(request);
        _experiments[created.Id] = created;
        return created;
    }

    public ReservationExperiment? Get(Guid id)
    {
        if (!_experiments.TryGetValue(id, out var experiment))
            return null;
        if (experiment.CreatedAtUtc >= DateTimeOffset.UtcNow.AddMinutes(-30)) 
            return experiment;
        _experiments.TryRemove(id, out _);
        return null;
    }
}

public sealed class ReservationExperiment
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly TaskCompletionSource _startSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<ReservationEvent> _events = new();
    private int _accepted;
    private int _completed;
    private int _reservations;
    private int _active;
    private int _maxConcurrent;
    private int _arrived;
    private long _eventSequence;

    public ReservationExperiment(CreateExperimentRequest request)
    {
        Mode = request.Mode;
        StartTogether = request.StartTogether;
        PlannedRequests = request.PlannedRequests;
        WorkDelayMs = request.WorkDelayMs;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public DateTimeOffset CreatedAtUtc { get; } = DateTimeOffset.UtcNow;
    public ReservationMode Mode { get; }
    public bool StartTogether { get; }
    public int PlannedRequests { get; }
    public int WorkDelayMs { get; }

    public bool TryAccept(out int requestNumber)
    {
        while (true)
        {
            var current = Volatile.Read(ref _accepted);
            if (current >= PlannedRequests) 
            { 
                requestNumber = 0;
                return false; 
            }
            if (Interlocked.CompareExchange(ref _accepted, current + 1, current) == current)
            { 
                requestNumber = current + 1; 
                return true; 
            }
        }
    }

    public async Task<ReservationResult> ReserveAsync(int requestNumber, string clientId, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var startThread = Environment.CurrentManagedThreadId;
        var active = Interlocked.Increment(ref _active);
        UpdateMaximum(active);
        Log(requestNumber, clientId, "started", null);
        try
        {
            // Coordinate arrival only. This does not protect the shared counter.
            if (StartTogether)
            {
                Log(requestNumber, clientId, "barrier", null);
                if (Interlocked.Increment(ref _arrived) >= Math.Min(PlannedRequests, 4))
                    _startSignal.TrySetResult();
                try 
                { 
                    await _startSignal.Task.WaitAsync(TimeSpan.FromSeconds(5), ct); 
                }
                catch (TimeoutException) { 
                    Log(requestNumber, clientId, "start-timeout", null); 
                }
            }

            int before;
            int after;
            switch (Mode)
            {
                case ReservationMode.Unsafe:
                    // LR2: two requests can read the same value before either writes it.
                    before = _reservations;
                    Log(requestNumber, clientId, "read", before);
                    await Task.Delay(WorkDelayMs, ct);
                    after = before + 1;
                    _reservations = after;
                    break;
                case ReservationMode.Lock:
                    // LR3: the entire read / work / write sequence is the critical section.
                    Log(requestNumber, clientId, "waiting", null);
                    lock (_gate)
                    {
                        before = _reservations;
                        Log(requestNumber, clientId, "read", before);
                        Thread.Sleep(WorkDelayMs);
                        after = before + 1;
                        _reservations = after;
                    }
                    break;
                case ReservationMode.SemaphoreSlim:
                    Log(requestNumber, clientId, "waiting", null);
                    await _semaphore.WaitAsync(ct);
                    try
                    {
                        before = _reservations;
                        Log(requestNumber, clientId, "read", before);
                        await Task.Delay(WorkDelayMs, ct);
                        after = before + 1;
                        _reservations = after;
                    }
                    finally { 
                        _semaphore.Release(); 
                    }
                    break;
                default: throw new UnreachableException();
            }
            Log(requestNumber, clientId, "wrote", after);
            Interlocked.Increment(ref _completed);
            return new ReservationResult(requestNumber, clientId, before, after,
                startThread, Environment.CurrentManagedThreadId, started, DateTimeOffset.UtcNow);
        }
        finally { 
            Interlocked.Decrement(ref _active); 
        }
    }

    public ExperimentSnapshot Snapshot()
    {
        var completed = Volatile.Read(ref _completed);
        var actual = Volatile.Read(ref _reservations);
        return new ExperimentSnapshot(Id, Mode, StartTogether, PlannedRequests, WorkDelayMs,
            Volatile.Read(ref _accepted), completed, completed, actual, completed - actual,
            Volatile.Read(ref _active), Volatile.Read(ref _maxConcurrent), Environment.ProcessId,
            _events.OrderBy(x => x.Sequence).ToArray());
    }

    private void Log(int number, string clientId, string phase, int? value) 
        => _events.Enqueue(new ReservationEvent(Interlocked.Increment(ref _eventSequence), number,
            clientId, phase, DateTimeOffset.UtcNow, Environment.CurrentManagedThreadId, value));

    private void UpdateMaximum(int active)
    {
        while (true)
        {
            var current = Volatile.Read(ref _maxConcurrent);
            if (active <= current || Interlocked.CompareExchange(ref _maxConcurrent, active, current) == current)
                return;
        }
    }
}
