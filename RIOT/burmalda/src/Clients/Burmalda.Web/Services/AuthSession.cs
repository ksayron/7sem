using System.Net.Http.Json;
using System.Text.Json;
using Burmalda.Web.Models;
using Microsoft.JSInterop;

namespace Burmalda.Web.Services;

public sealed class AuthSession(HttpClient http, IJSRuntime js)
{
    private const string StorageKey = "burmalda.session";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public UserSession? Current { get; private set; }
    public bool IsAuthenticated => Current is { } session && session.ExpiresAt > DateTimeOffset.UtcNow;
    public bool IsAdmin => Current?.Role is "Admin";
    public event Action? Changed;

    public async Task InitializeAsync()
    {
        var json = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            Current = JsonSerializer.Deserialize<UserSession>(json, JsonOptions);
            if (!IsAuthenticated) await ClearAsync();
        }
        catch (JsonException)
        {
            await ClearAsync();
        }
    }

    public async Task LoginAsync(LoginForm form, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("/api/identity/login", form, ct);
        await EnsureSuccess(response, ct);
        Current = await response.Content.ReadFromJsonAsync<UserSession>(JsonOptions, ct)
            ?? throw new ApiException("The server returned an empty session.");
        await PersistAsync();
    }

    public async Task RegisterAsync(RegisterForm form, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("/api/identity/register", form, ct);
        await EnsureSuccess(response, ct);
        await LoginAsync(new LoginForm { Email = form.Email, Password = form.Password }, ct);
    }

    public async Task LogoutAsync()
    {
        if (Current is not null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/identity/logout")
            {
                Content = JsonContent.Create(new { Current.RefreshToken })
            };
            request.Headers.Authorization = new("Bearer", Current.AccessToken);
            try { await http.SendAsync(request); } catch (HttpRequestException) { }
        }
        await ClearAsync();
    }

    private async Task PersistAsync()
    {
        await js.InvokeVoidAsync("localStorage.setItem", StorageKey, JsonSerializer.Serialize(Current, JsonOptions));
        Changed?.Invoke();
    }

    private async Task ClearAsync()
    {
        Current = null;
        await js.InvokeVoidAsync("localStorage.removeItem", StorageKey);
        Changed?.Invoke();
    }

    internal static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var message = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
            ? "The email or password is incorrect."
            : $"Request failed ({(int)response.StatusCode}).";
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ApiProblem>(JsonOptions, ct);
            message = problem?.Message ?? problem?.Title ?? problem?.Detail ?? message;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
        throw new ApiException(message);
    }

    private sealed record ApiProblem(string? Message, string? Title, string? Detail);
}

public sealed class ApiException(string message) : Exception(message);
