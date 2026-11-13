using System.Data.Common;
using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Burmalda.Persistence;

public static class DatabaseAvailability
{
    public static DbContextOptionsBuilder WithDatabaseFailover(this DbContextOptionsBuilder options)
        => options.AddInterceptors(new FailoverConnectionInterceptor());

    public static bool IsUnavailable(Exception error) => error switch
    {
        DatabaseUnavailableException => true,
        PostgresException sql => sql.IsTransient || sql.SqlState is "57P01" or "57P02" or "57P03" or "25006",
        // Multi-host exhaustion can have IsTransient=false, including the no-inner
        // exception case where all hosts were filtered out as read-only/offline.
        NpgsqlException { InnerException: AggregateException hosts } => hosts.InnerExceptions.All(e => e is OperationCanceledException || IsUnavailable(e)),
        NpgsqlException { InnerException: PostgresException sql } => IsUnavailable(sql),
        NpgsqlException => true,
        DbException db => db.IsTransient,
        DbUpdateException { InnerException: { } inner } => IsUnavailable(inner),
        System.Net.Sockets.SocketException or IOException or TimeoutException => true,
        _ => false
    };
}

public sealed class DatabaseUnavailableException(Exception? inner)
    : Exception("Writable PostgreSQL did not become available within the connection wait budget.", inner);

// Only opening a connection is retried. No SQL statement or transaction is replayed.
public sealed class FailoverConnectionInterceptor(TimeSpan? waitBudget = null, TimeSpan? retryDelay = null) : DbConnectionInterceptor
{
    private readonly TimeSpan budget = waitBudget ?? TimeSpan.FromSeconds(15);
    private readonly TimeSpan delay = retryDelay ?? TimeSpan.FromMilliseconds(250);

    public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
        ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        if (result.IsSuppressed) return result;
        await Open(connection, cancellationToken);
        return InterceptionResult.Suppress();
    }

    public async Task Open(DbConnection connection, CancellationToken ct)
    {
        // Npgsql's schema existence check invokes interceptors even when a schema-lock
        // connection is already open. Retain that session and its advisory lock.
        if (connection.State == System.Data.ConnectionState.Open) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(budget);
        Exception? last = null;
        var elapsed = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                try { await connection.OpenAsync(deadline.Token); return; }
                catch (DbException ex) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
                { throw new DatabaseUnavailableException(ex); }
                catch (DbException ex) when (DatabaseAvailability.IsUnavailable(ex))
                {
                    last = ex;
                    await connection.CloseAsync();
                    if (elapsed.Elapsed >= budget) throw new DatabaseUnavailableException(last);
                    await Task.Delay(delay, deadline.Token);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new DatabaseUnavailableException(last); }
    }
}

public sealed class DatabaseUnavailableHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (context.RequestAborted.IsCancellationRequested || !DatabaseAvailability.IsUnavailable(exception)) return false;
        const string message = "База данных временно недоступна или выполняется переключение PostgreSQL. Обновите состояние перед повтором операции.";
        context.Response.Headers.RetryAfter = "2";
        await Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "PostgreSQL временно недоступен", detail: message,
            extensions: new Dictionary<string, object?> { ["message"] = message }).ExecuteAsync(context);
        return true;
    }
}
