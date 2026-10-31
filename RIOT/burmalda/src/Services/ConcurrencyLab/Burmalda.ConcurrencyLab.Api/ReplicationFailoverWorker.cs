namespace Burmalda.ConcurrencyLab.Api;

public sealed class ReplicationFailoverWorker(ReplicationLabStore store, ILogger<ReplicationFailoverWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        string? lastError = null;
        do
        {
            try { await store.MaintainWriter(stoppingToken); lastError = null; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Keep the coordinator alive, without flooding logs while replay is paused.
                if (lastError != ex.Message)
                    logger.LogWarning("Automatic failover deferred ({Kind}): {Message}", ex.GetType().Name, ex.Message);
                lastError = ex.Message;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        } while (!stoppingToken.IsCancellationRequested);
    }
}
