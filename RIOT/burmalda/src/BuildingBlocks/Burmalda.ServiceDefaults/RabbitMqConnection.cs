using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Burmalda.ServiceDefaults;

public sealed class RabbitMqConnection(IOptions<RabbitMqOptions> options) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public async Task<IConnection> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is { IsOpen: true }) return _connection;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: true }) return _connection;
            var value = options.Value;
            var factory = new ConnectionFactory
            {
                HostName = value.Host,
                Port = value.Port,
                UserName = value.UserName,
                Password = value.Password,
                VirtualHost = value.VirtualHost,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                ClientProvidedName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "burmalda-service"
            };
            _connection = await factory.CreateConnectionAsync(cancellationToken);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
        _gate.Dispose();
    }
}
