using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Burmalda.LibraryGame;

namespace Burmalda.Web.Services;

public sealed class LibraryGameClient(HttpClient http, AuthSession auth)
{
    private readonly string clientId = $"web-{Guid.NewGuid():N}";
    public Task<GameSnapshot> Snapshot(bool demo = false) => Send<GameSnapshot>(HttpMethod.Get, $"state?demo={demo}");
    public Task<GameState> Command(GameState state, string type, string? book = null, int? series = null, string? upgrade = null) =>
        Send<GameState>(HttpMethod.Post, $"commands?demo={state.Demo}", new GameCommand(Guid.NewGuid(), state.Version, type, book, series, upgrade, clientId));
    public Task<GameState> Reset(GameState state) => Send<GameState>(HttpMethod.Post, $"reset?demo={state.Demo}", new { state.SessionId });
    public Task<JsonElement> Lab() => Send<JsonElement>(HttpMethod.Get, "lab");
    public Task<JsonElement> Control(string subscriber, SubscriberControl control) => Send<JsonElement>(HttpMethod.Post, "lab/control", new { subscriber, control });
    public Task<JsonElement> Analytics() => Send<JsonElement>(HttpMethod.Post, "lab/analytics");
    public Task<JsonElement> Replay() => Send<JsonElement>(HttpMethod.Post, "lab/replay");
    public Task<JsonElement> RetryDelivery(bool demo) => Send<JsonElement>(HttpMethod.Post, $"retry-delivery?demo={demo}");
    private async Task<T> Send<T>(HttpMethod method, string path, object? body = null)
    {
        if (!auth.IsAuthenticated) throw new ApiException("Войдите в аккаунт Burmalda, чтобы сохранить прогресс.");
        using var request = new HttpRequestMessage(method, $"/api/library-game/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Current!.AccessToken);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAvailableAsync(request);
        await AuthSession.EnsureSuccess(response, default);
        var content = await response.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(content) ? default! : GameJson.Read<T>(content);
    }
}

