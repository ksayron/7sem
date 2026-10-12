using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Burmalda.ConcurrencyLab.Api;

public enum DatabaseLabMode { Unsafe, Lock, NoTransaction, Transaction, Saga }
public enum LabFailure { None, AfterOperation1, BeforeCommit, Points, Loan }
public sealed record CreateDatabaseRun(DatabaseLabMode Mode, LabFailure Failure = LabFailure.None);
public sealed record DatabaseReserveRequest(string ClientId, DateTimeOffset? SentAtUtc = null);
public sealed record DatabaseState(bool CopyAvailable, int Loans, int ReservedPoints);
public sealed record DatabaseEvent(long Sequence, DateTimeOffset AtUtc, string ClientId, string Level, string Phase, string Detail);
public sealed record DatabaseClientResult(string ClientId, DateTimeOffset? SentAtUtc, DateTimeOffset StartedAtUtc,
    DateTimeOffset? EnteredAtUtc, DateTimeOffset? ExitedAtUtc, string Result);
public sealed record DatabaseSnapshot(Guid Id, DatabaseLabMode Mode, LabFailure Failure, string Status,
    DatabaseState Before, DatabaseState After, IReadOnlyList<DatabaseEvent> Events, IReadOnlyList<DatabaseClientResult> Clients);

public sealed class LabCopy
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "Учебный экземпляр Burmalda";
    public bool Available { get; set; } = true;
    public int ReservedPoints { get; set; }
}
public sealed class LabLoan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CopyId { get; set; }
    public required string ClientId { get; set; }
}
public sealed class LabDbContext(DbContextOptions<LabDbContext> options) : DbContext(options)
{
    public DbSet<LabCopy> Copies => Set<LabCopy>();
    public DbSet<LabLoan> Loans => Set<LabLoan>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<LabCopy>(e =>
        {
            e.ToTable("lab_copies", t => t.HasCheckConstraint("ck_points", "\"ReservedPoints\" >= 0"));
            e.HasKey(x => x.Id); e.Property(x => x.Title).HasMaxLength(200).IsRequired();
        });
        model.Entity<LabLoan>(e =>
        {
            e.ToTable("lab_loans"); e.HasKey(x => x.Id); e.Property(x => x.ClientId).HasMaxLength(32).IsRequired();
            e.HasOne<LabCopy>().WithMany().HasForeignKey(x => x.CopyId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public sealed class DatabaseLabStore(IDbContextFactory<LabDbContext> factory, ILogger<DatabaseLabStore> logger)
{
    private readonly ConcurrentDictionary<Guid, DatabaseRun> _runs = new();
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private bool _ready;

    public async Task<DatabaseRun> Create(CreateDatabaseRun request, CancellationToken ct)
    {
        await _initialization.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            if (!_ready) { await db.Database.EnsureCreatedAsync(ct); _ready = true; }
            // Delete only expired, idle lab fixtures; production services own separate databases.
            foreach (var (id, old) in _runs.Where(x => x.Value.CreatedAtUtc < DateTimeOffset.UtcNow.AddHours(-2) && x.Value.IsIdle))
            {
                await db.Copies.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
                _runs.TryRemove(id, out _);
            }
            if (_runs.Count >= 200) throw new LabConflictException("Лимит опытов: дождитесь удаления старых учебных данных через 2 часа.");
            var run = new DatabaseRun(request, factory, logger);
            db.Copies.Add(new LabCopy { Id = run.Id });
            await db.SaveChangesAsync(ct);
            _runs[run.Id] = run;
            run.Log("server", "info", "READY", "PostgreSQL: Resource = 1; Loans = 0; Points = 0");
            return run;
        }
        finally { _initialization.Release(); }
    }

    public DatabaseRun Get(Guid id) => _runs.TryGetValue(id, out var run) ? run : throw new LabNotFoundException();
}

public sealed class LabConflictException(string message) : Exception(message);
public sealed class LabNotFoundException : Exception;
public sealed class InjectedLabException : Exception;

public sealed class DatabaseRun(CreateDatabaseRun request, IDbContextFactory<LabDbContext> factory, ILogger logger)
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _execution = new(1, 1);
    private readonly ConcurrentQueue<DatabaseEvent> _events = new();
    private readonly ConcurrentQueue<DatabaseClientResult> _clients = new();
    private readonly ConcurrentDictionary<string, byte> _accepted = new();
    private readonly TaskCompletionSource _together = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _bothRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _readers;
    private int _active;
    private long _sequence;
    private string _status = "Ready";
    private int _sagaStep;
    public Guid Id { get; } = Guid.NewGuid();
    public DateTimeOffset CreatedAtUtc { get; } = DateTimeOffset.UtcNow;
    public bool IsIdle => Volatile.Read(ref _active) == 0 && _execution.CurrentCount == 1;

    public void Log(string client, string level, string phase, string detail)
    {
        _events.Enqueue(new(Interlocked.Increment(ref _sequence), DateTimeOffset.UtcNow, client, level, phase, detail));
        logger.Log(level == "error" ? LogLevel.Error : level == "warn" ? LogLevel.Warning : LogLevel.Information,
            "Lab {Run} · {Client} → {Phase} · {Detail}", Id.ToString()[..8], client, phase, detail);
    }

    public async Task<DatabaseSnapshot> Snapshot(CancellationToken ct = default)
    {
        // A new context reads persisted data rather than EF's tracked changes.
        await using var db = await factory.CreateDbContextAsync(ct);
        var copy = await db.Copies.AsNoTracking().SingleOrDefaultAsync(x => x.Id == Id, ct) ?? throw new LabNotFoundException();
        var count = await db.Loans.CountAsync(x => x.CopyId == Id, ct);
        return new(Id, request.Mode, request.Failure, _status, new(true, 0, 0), new(copy.Available, count, copy.ReservedPoints),
            _events.OrderBy(x => x.Sequence).ToArray(), _clients.OrderBy(x => x.StartedAtUtc).ToArray());
    }

    public async Task<DatabaseSnapshot> Reserve(DatabaseReserveRequest input, CancellationToken ct)
    {
        if (request.Mode is not (DatabaseLabMode.Unsafe or DatabaseLabMode.Lock))
            throw new LabConflictException("Этот опыт не предназначен для конкурентного резервирования.");
        lock (_accepted)
        {
            if (_accepted.Count >= 2 || !_accepted.TryAdd(input.ClientId, 0))
                throw new LabConflictException("Нужны два разных клиента; повторный запрос не допускается.");
            if (_accepted.Count == 2) _together.TrySetResult();
        }
        Interlocked.Increment(ref _active);
        var started = DateTimeOffset.UtcNow;
        Log(input.ClientId, "info", "START Reserve", "Ожидание второго клиента");
        DateTimeOffset? entered = null;
        DateTimeOffset? exited = null;
        _status = "Running";
        var result = "ERROR";
        try
        {
            await _together.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            if (request.Mode == DatabaseLabMode.Lock)
            {
                Log(input.ClientId, "info", "WAIT LOCK", "Критическая секция: check → reserve → save");
                // lock cannot contain await. Only the related DB read/check/write is protected.
                // The gate belongs to this fixture and all its requests in this server process.
                lock (_gate)
                {
                    entered = DateTimeOffset.UtcNow;
                    Log(input.ClientId, "info", "LOCK", "Вход в критическую секцию");
                    try
                    {
                        using var db = factory.CreateDbContext();
                        var copy = db.Copies.SingleOrDefault(x => x.Id == Id) ?? throw new LabNotFoundException();
                        Log(input.ClientId, "info", "CHECK", $"Available = {copy.Available}");
                        if (copy.Available)
                        {
                            copy.Available = false;
                            db.Loans.Add(new() { CopyId = Id, ClientId = input.ClientId });
                            db.SaveChanges();
                            result = "SUCCESS";
                        }
                        else result = "FAIL";
                        Log(input.ClientId, result == "FAIL" ? "warn" : "info", result, "Reserve");
                    }
                    finally
                    {
                        exited = DateTimeOffset.UtcNow;
                        Log(input.ClientId, "info", "UNLOCK", "Выход из критической секции");
                    }
                }
            }
            else
            {
                entered = DateTimeOffset.UtcNow;
                await using var db = await factory.CreateDbContextAsync(ct);
                var copy = await db.Copies.SingleOrDefaultAsync(x => x.Id == Id, ct) ?? throw new LabNotFoundException();
                Log(input.ClientId, "info", "CHECK", $"Available = {copy.Available}");
                // Both requests read the single available copy before either writes.
                if (Interlocked.Increment(ref _readers) == 2) _bothRead.TrySetResult();
                await _bothRead.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                if (copy.Available)
                {
                    copy.Available = false;
                    db.Loans.Add(new() { CopyId = Id, ClientId = input.ClientId });
                    await db.SaveChangesAsync(ct);
                    result = "SUCCESS";
                }
                else result = "FAIL";
                exited = DateTimeOffset.UtcNow;
                Log(input.ClientId, "warn", result, "Без синхронизации");
            }
        }
        catch (TimeoutException) { Log(input.ClientId, "error", "TIMEOUT", "Второй клиент не выполнил запрос вовремя"); }
        finally
        {
            _clients.Enqueue(new(input.ClientId, input.SentAtUtc, started, entered, exited, result));
            if (Interlocked.Decrement(ref _active) == 0 && _accepted.Count == 2)
                _status = _clients.Count(x => x.Result == "SUCCESS") == 1 && _clients.Count(x => x.Result == "FAIL") == 1 ? "Correct" : "Incorrect";
        }
        return await Snapshot(ct);
    }

    public async Task<DatabaseSnapshot> Execute(CancellationToken ct)
    {
        if (request.Mode is not (DatabaseLabMode.NoTransaction or DatabaseLabMode.Transaction))
            throw new LabConflictException("Используйте reserve или step для выбранного механизма.");
        if (!await _execution.WaitAsync(0, ct)) throw new LabConflictException("Опыт уже выполняется.");
        try
        {
            if (_status != "Ready") throw new LabConflictException("Опыт уже завершён. Создайте новый для повторения.");
            _status = "Running";
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var transaction = request.Mode == DatabaseLabMode.Transaction ? await db.Database.BeginTransactionAsync(ct) : null;
            Log("server", "info", transaction is null ? "AUTOCOMMIT" : "BEGIN", "Операция 1: создать выдачу; операция 2: занять экземпляр");
            var operation1Saved = false;
            try
            {
                db.Loans.Add(new() { CopyId = Id, ClientId = "Client 1" });
                await db.SaveChangesAsync(ct);
                operation1Saved = true;
                Log("Client 1", "info", "OPERATION 1", "Выдача добавлена");
                await Task.Delay(350, ct);
                if (request.Failure == LabFailure.AfterOperation1) throw new InjectedLabException();
                var copy = await db.Copies.SingleOrDefaultAsync(x => x.Id == Id, ct) ?? throw new LabNotFoundException();
                copy.Available = false;
                await db.SaveChangesAsync(ct);
                Log("Client 1", "info", "OPERATION 2", "Экземпляр занят");
                if (request.Failure == LabFailure.BeforeCommit) throw new InjectedLabException();
                if (transaction is not null) await transaction.CommitAsync(ct);
                _status = transaction is null ? "Saved" : "Committed";
                Log("server", "info", transaction is null ? "SAVED" : "COMMIT", "Обе операции сохранены");
            }
            catch (Exception ex) when (ex is InjectedLabException or DbUpdateException or System.Data.Common.DbException or OperationCanceledException)
            {
                Log("server", "error", "ERROR", ex is InjectedLabException ? "Искусственная ошибка" : "Ошибка БД или отмена операции");
                if (transaction is not null)
                {
                    try { await transaction.RollbackAsync(CancellationToken.None); }
                    catch (System.Data.Common.DbException)
                    {
                        _status = "RecoveryRequired";
                        Log("server", "error", "ROLLBACK FAILED", "Соединение потеряно; результат требует проверки БД");
                        throw;
                    }
                    _status = "RolledBack";
                    Log("server", "warn", "ROLLBACK", "Изменения обеих операций отменены");
                }
                else
                {
                    _status = operation1Saved ? "Partial" : "Failed";
                    Log("server", "warn", operation1Saved ? "PARTIAL" : "FAILED", operation1Saved
                        ? "Первое SaveChanges уже сохранило выдачу; экземпляр остался свободным"
                        : "Первая операция не сохранена");
                }
            }
            // Dispose the transaction before querying persisted state with another connection.
            if (transaction is not null) await transaction.DisposeAsync();
            return await Snapshot(CancellationToken.None);
        }
        finally { _execution.Release(); }
    }

    public async Task<DatabaseSnapshot> Step(CancellationToken ct)
    {
        if (request.Mode != DatabaseLabMode.Saga) throw new LabConflictException("Пошаговый запуск доступен для саги.");
        if (!await _execution.WaitAsync(0, ct)) throw new LabConflictException("Шаг уже выполняется.");
        try
        {
            if (_status is "Completed" or "Compensated") throw new LabConflictException("Сага завершена. Создайте новый опыт.");
            await using var db = await factory.CreateDbContextAsync(ct);
            // Each step is a separate committed local transaction. Compensation is a new write.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var copy = await db.Copies.SingleOrDefaultAsync(x => x.Id == Id, ct) ?? throw new LabNotFoundException();
            var nextStatus = _status;
            var nextStep = _sagaStep + 1;
            string phase;
            string detail;
            string level = "info";
            switch (_sagaStep)
            {
                case 0:
                    copy.Available = false; phase = "ReserveBookCopy"; detail = "Catalog: экземпляр зарезервирован; локальный COMMIT"; nextStatus = "AwaitingPointsReservation"; break;
                case 1 when request.Failure == LabFailure.Points:
                    phase = "PointsReservationFailed"; detail = "Economy: искусственный отказ → компенсация книги"; nextStatus = "CompensatingCopy"; nextStep = 4; level = "error"; break;
                case 1:
                    copy.ReservedPoints = 10; phase = "ReservePoints"; detail = "Economy: 10 BD зарезервированы; локальный COMMIT"; nextStatus = "CreatingLoan"; break;
                case 2 when request.Failure == LabFailure.Loan:
                    phase = "LoanCreationFailed"; detail = "Lending: искусственный отказ → обратные действия"; nextStatus = "CompensatingPoints"; level = "error"; break;
                case 2:
                    db.Loans.Add(new() { CopyId = Id, ClientId = "Client 1" }); phase = "CreateLoan"; detail = "Lending: выдача создана; локальный COMMIT"; nextStatus = "Completed"; break;
                case 3:
                    copy.ReservedPoints = 0; phase = "ReleasePoints"; detail = "Economy: резерв 10 BD снят новым COMMIT"; nextStatus = "CompensatingCopy"; break;
                default:
                    copy.Available = true; phase = "ReleaseBookCopy"; detail = "Catalog: экземпляр освобождён новым COMMIT"; nextStatus = "Compensated"; break;
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _status = nextStatus; _sagaStep = nextStep;
            Log("saga", level, phase, detail);
            return await Snapshot(ct);
        }
        finally { _execution.Release(); }
    }
}

public static class DatabaseLabEndpoints
{
    public static IEndpointRouteBuilder MapDatabaseLab(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/concurrency-lab/database");
        group.MapPost("/runs", async (CreateDatabaseRun request, DatabaseLabStore store, CancellationToken ct) =>
        {
            if (!Enum.IsDefined(request.Mode) || !Enum.IsDefined(request.Failure) ||
                (request.Mode is DatabaseLabMode.Unsafe or DatabaseLabMode.Lock && request.Failure != LabFailure.None) ||
                (request.Mode is DatabaseLabMode.NoTransaction or DatabaseLabMode.Transaction && request.Failure is LabFailure.Points or LabFailure.Loan) ||
                (request.Mode == DatabaseLabMode.NoTransaction && request.Failure == LabFailure.BeforeCommit) ||
                (request.Mode == DatabaseLabMode.Saga && request.Failure is LabFailure.AfterOperation1 or LabFailure.BeforeCommit))
                return Results.BadRequest(new { message = "Недопустимый механизм или место искусственной ошибки." });
            var run = await store.Create(request, ct);
            return Results.Created($"/api/concurrency-lab/database/runs/{run.Id}", await run.Snapshot(ct));
        });
        group.MapGet("/runs/{id:guid}", async (Guid id, DatabaseLabStore store, CancellationToken ct) => Results.Ok(await store.Get(id).Snapshot(ct)));
        group.MapPost("/runs/{id:guid}/reserve", async (Guid id, DatabaseReserveRequest input, DatabaseLabStore store, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.ClientId) || input.ClientId.Length > 32)
                return Results.BadRequest(new { message = "ClientId: от 1 до 32 символов." });
            return Results.Ok(await store.Get(id).Reserve(input, ct));
        });
        group.MapPost("/runs/{id:guid}/execute", async (Guid id, DatabaseLabStore store, CancellationToken ct) => Results.Ok(await store.Get(id).Execute(ct)));
        group.MapPost("/runs/{id:guid}/step", async (Guid id, DatabaseLabStore store, CancellationToken ct) => Results.Ok(await store.Get(id).Step(ct)));
        return app;
    }
}
