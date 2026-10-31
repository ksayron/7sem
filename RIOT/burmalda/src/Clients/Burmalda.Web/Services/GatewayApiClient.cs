using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Burmalda.Web.Models;

namespace Burmalda.Web.Services;

public sealed class GatewayApiClient(HttpClient http, AuthSession auth)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<IReadOnlyList<BookSearchItem>> SearchBooksAsync(string query, CancellationToken ct = default) =>
        GetList<BookSearchItem>($"/api/catalog/books/search?q={Uri.EscapeDataString(query)}", ct);

    public Task<IReadOnlyList<BookCopyItem>> GetCopiesAsync(Guid bookId, CancellationToken ct = default) =>
        GetList<BookCopyItem>($"/api/catalog/books/{bookId}/copies", ct);

    public Task<IReadOnlyList<OwnedCopy>> GetMyCopiesAsync(CancellationToken ct = default) =>
        GetList<OwnedCopy>("/api/catalog/copies/mine", ct);

    public Task<IReadOnlyList<LoanItem>> GetMyLoansAsync(CancellationToken ct = default) =>
        GetList<LoanItem>("/api/loans/mine", ct);

    public Task<IReadOnlyList<TransactionItem>> GetTransactionsAsync(CancellationToken ct = default) =>
        GetList<TransactionItem>("/api/economy/transactions", ct);

    public Task<IReadOnlyList<NotificationItem>> GetNotificationsAsync(CancellationToken ct = default) =>
        GetList<NotificationItem>("/api/notifications/", ct);

    public Task<WalletSummary> GetWalletAsync(CancellationToken ct = default) => Get<WalletSummary>("/api/economy/wallet", ct);
    public Task<ReputationSummary> GetReputationAsync(CancellationToken ct = default) => Get<ReputationSummary>("/api/reputation/me", ct);
    public Task<CountResult> GetUnreadCountAsync(CancellationToken ct = default) => Get<CountResult>("/api/notifications/unread-count", ct);
    public Task<SystemHealth> GetSystemHealthAsync(CancellationToken ct = default) => Get<SystemHealth>("/api/admin/system/health", ct);

    public Task<CreatedBook> CreateBookAsync(CreateBookForm form, CancellationToken ct = default) => Post<CreatedBook>("/api/catalog/books", new
    {
        form.Isbn, form.Title, form.Description,
        Authors = Split(form.Authors), Genres = Split(form.Genres),
        form.Publisher, form.PublicationYear, CoverUrl = (string?)null
    }, ct);

    public Task<CreatedId> ListCopyAsync(ListCopyForm form, CancellationToken ct = default) => Post<CreatedId>("/api/catalog/copies", new
    {
        BookId = form.BookId!.Value, form.Condition, form.LendingPrice, form.MaximumLoanDays, form.Location
    }, ct);

    public Task<LoanRequestReceipt> RequestLoanAsync(Guid copyId, CancellationToken ct = default) =>
        Post<LoanRequestReceipt>("/api/loans/requests", new { CopyId = copyId }, ct);

    public Task<LoanSagaSnapshot> GetLoanSagaAsync(Guid id, CancellationToken ct = default) =>
        Get<LoanSagaSnapshot>($"/api/loans/requests/{id}", ct);

    public Task MarkNotificationReadAsync(Guid id, CancellationToken ct = default) => SendNoContent(HttpMethod.Put, $"/api/notifications/{id}/read", null, ct);
    public Task MarkAllNotificationsReadAsync(CancellationToken ct = default) => SendNoContent(HttpMethod.Put, "/api/notifications/read-all", null, ct);
    public Task CancelLoanAsync(Guid id, CancellationToken ct = default) => SendNoContent(HttpMethod.Post, $"/api/loans/{id}/cancel", new { }, ct);
    public Task RejectLoanAsync(Guid id, string reason, CancellationToken ct = default) => SendNoContent(HttpMethod.Post, $"/api/loans/{id}/reject", new { Reason = reason }, ct);
    public Task InitiateReturnAsync(Guid id, CancellationToken ct = default) => SendNoContent(HttpMethod.Post, $"/api/loans/{id}/return", new { }, ct);
    public Task ConfirmReturnAsync(Guid id, CancellationToken ct = default) => SendNoContent(HttpMethod.Post, $"/api/loans/{id}/return/confirm", new { }, ct);

    public async Task<string> AcceptLoanAsync(Guid id, CancellationToken ct = default)
    {
        var result = await Post<AcceptLoanResult>($"/api/loans/{id}/accept", new { }, ct);
        return result.HandoverCode;
    }

    public Task ConfirmHandoverAsync(Guid id, string code, CancellationToken ct = default) =>
        SendNoContent(HttpMethod.Post, $"/api/loans/{id}/handover/confirm", new { Code = code }, ct);

    private async Task<T> Get<T>(string path, CancellationToken ct)
    {
        using var response = await Send(HttpMethod.Get, path, null, ct);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct) ?? throw new ApiException("The server returned no data.");
    }

    private async Task<IReadOnlyList<T>> GetList<T>(string path, CancellationToken ct)
    {
        using var response = await Send(HttpMethod.Get, path, null, ct);
        return await response.Content.ReadFromJsonAsync<List<T>>(JsonOptions, ct) ?? [];
    }

    private async Task<T> Post<T>(string path, object body, CancellationToken ct)
    {
        using var response = await Send(HttpMethod.Post, path, body, ct);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct) ?? throw new ApiException("The server returned no data.");
    }

    private async Task SendNoContent(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await Send(method, path, body, ct);
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        if (!auth.IsAuthenticated) throw new ApiException("Sign in to continue.");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Current!.AccessToken);
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await http.SendAvailableAsync(request, ct);
        try { await AuthSession.EnsureSuccess(response, ct); }
        catch { response.Dispose(); throw; }
        return response;
    }

    private static string[] Split(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private sealed record AcceptLoanResult(string HandoverCode);
}
