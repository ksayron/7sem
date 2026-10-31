namespace Burmalda.Web.Services;

// Catch outside HttpClient's timeout token so caller cancellation remains distinct.
// Requests, especially POSTs, are never automatically resent here.
public static class ApiAvailability
{
    public static async Task<HttpResponseMessage> SendAvailableAsync(this HttpClient client,
        HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        try { return await client.SendAsync(request, cancellationToken); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new ApiException("Сервер не ответил вовремя. Обновите состояние перед повтором операции."); }
    }
}
