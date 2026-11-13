using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Burmalda.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Burmalda.Domain.Tests;

public sealed class DatabaseAvailabilityTests
{
    // Success-path checks must tolerate scheduling delays during container builds.
    // Deadline behavior is covered separately with an explicit short budget.
    private static FailoverConnectionInterceptor Policy(int budget = 2000) => new(TimeSpan.FromMilliseconds(budget), TimeSpan.FromMilliseconds(5));
    [Fact]
    public async Task An_open_schema_lock_connection_is_retained()
    {
        await using var connection = new Connection();
        await connection.OpenAsync(default);
        await Policy().Open(connection, default);
        Assert.Equal(1, connection.Attempts);
        Assert.Equal(ConnectionState.Open, connection.State);
    }
    [Fact]
    public async Task Retries_connection_open_until_a_writer_is_available()
    {
        await using var connection = new Connection { Failures = 2 };
        await Policy().Open(connection, default);
        Assert.Equal(3, connection.Attempts); Assert.Equal(ConnectionState.Open, connection.State);
    }
    [Fact]
    public async Task Permanent_authentication_or_configuration_error_is_not_retried()
    {
        await using var connection = new Connection { Failures = 10, Transient = false };
        await Assert.ThrowsAsync<Unavailable>(() => Policy().Open(connection, default));
        Assert.Equal(1, connection.Attempts);
    }
    [Fact]
    public async Task Exhausted_budget_returns_a_classifiable_unavailable_error()
    {
        await using var connection = new Connection { Failures = int.MaxValue };
        var error = await Assert.ThrowsAsync<DatabaseUnavailableException>(() => Policy(30).Open(connection, default));
        Assert.True(DatabaseAvailability.IsUnavailable(error));
        Assert.True(DatabaseAvailability.IsUnavailable(new DbUpdateException("write failed", new Unavailable(true))));
        Assert.False(DatabaseAvailability.IsUnavailable(new Unavailable(false)));
    }
    [Fact]
    public async Task Caller_cancellation_is_preserved()
    {
        await using var connection = new Connection { Failures = int.MaxValue };
        using var ct = new CancellationTokenSource(20);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Policy().Open(connection, ct.Token));
    }
    [Fact]
    public void Npgsql_multi_host_exhaustion_is_unavailable_even_when_provider_marks_it_non_transient()
    {
        Assert.True(DatabaseAvailability.IsUnavailable(new Npgsql.NpgsqlException("No suitable host was found.")));
        Assert.True(DatabaseAvailability.IsUnavailable(new Npgsql.NpgsqlException("No writable host",
            new AggregateException(new Npgsql.NpgsqlException("Connection refused", new System.Net.Sockets.SocketException())))));
        Assert.True(DatabaseAvailability.IsUnavailable(new Npgsql.NpgsqlException("Deadline expired",
            new AggregateException(new Npgsql.NpgsqlException("Connection refused"), new OperationCanceledException()))));
        Assert.False(DatabaseAvailability.IsUnavailable(new Npgsql.NpgsqlException("All hosts failed",
            new AggregateException(new Npgsql.PostgresException("Invalid password", "FATAL", "FATAL", "28P01")))));
    }
    private sealed class Unavailable(bool transient) : DbException
    { public override bool IsTransient => transient; }
    private sealed class Connection : DbConnection
    {
        public int Failures { get; init; }
        public bool Transient { get; init; } = true;
        public int Attempts { get; private set; }
        private ConnectionState state;
        [AllowNull] public override string ConnectionString { get; set; } = "";
        public override string Database => "test";
        public override string DataSource => "test";
        public override string ServerVersion => "1";
        public override ConnectionState State => state;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() => state = ConnectionState.Closed;
        public override void Open() => throw new NotSupportedException();
        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++Attempts <= Failures) throw new Unavailable(Transient);
            state = ConnectionState.Open; return Task.CompletedTask;
        }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }
}
