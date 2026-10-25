using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace StackApi;

public interface IStackStore
{
    StackState Apply(HttpContext context, Func<StackState, StackState> operation);
}

public static class LabCookies
{
    public static CookieOptions Options(HttpContext context) => new()
    {
        HttpOnly = true,
        IsEssential = true,
        SameSite = SameSiteMode.Strict,
        Secure = context.Request.IsHttps,
        Path = "/stack"
    };
}

public sealed class ServerStackStore(IMemoryCache cache) : IStackStore
{
    public const string CookieName = "WS01.Session";
    private readonly object creationLock = new();

    private sealed class SessionEntry
    {
        public StackState State { get; set; } = StackState.Empty();
    }

    public StackState Apply(HttpContext context, Func<StackState, StackState> operation)
    {
        var id = context.Request.Cookies[CookieName];
        SessionEntry entry;
        lock (creationLock)
        {
            if (id is null || id.Length != 64 || !cache.TryGetValue(id, out SessionEntry? existing) || existing is null)
            {
                id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                entry = new SessionEntry();
                cache.Set(id, entry, TimeSpan.FromMinutes(30));
            }
            else
            {
                entry = existing;
                cache.Set(id, entry, TimeSpan.FromMinutes(30));
            }
        }

        lock (entry)
        {
            var next = operation(entry.State.Copy());
            entry.State = next;
            context.Response.Cookies.Append(CookieName, id, LabCookies.Options(context));
            return next;
        }
    }
}

public sealed class CookieStackStore(IConfiguration configuration) : IStackStore
{
    public const string CookieName = "WS02.State";
    private readonly byte[] key = Encoding.UTF8.GetBytes(configuration["COOKIE_SIGNING_KEY"]
        ?? "secret-key");

    public StackState Apply(HttpContext context, Func<StackState, StackState> operation)
    {
        var state = Read(context.Request.Cookies[CookieName]);
        var next = operation(state);
        var payload = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(next));
        var signature = WebEncoders.Base64UrlEncode(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload)));
        context.Response.Cookies.Append(CookieName, $"{payload}.{signature}", LabCookies.Options(context));
        return next;
    }

    private StackState Read(string? cookie)
    {
        if (cookie is null) 
            return StackState.Empty();
        try
        {
            if (cookie.Length > 3000) 
                throw new FormatException();
            var parts = cookie.Split('.');
            if (parts.Length != 2) 
                throw new FormatException();
            var actual = WebEncoders.Base64UrlDecode(parts[1]);
            var expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(parts[0]));
            if (!CryptographicOperations.FixedTimeEquals(actual, expected)) 
                throw new FormatException();
            var state = JsonSerializer.Deserialize<StackState>(WebEncoders.Base64UrlDecode(parts[0]));
            if (state?.Stack is null || state.Stack.Count > LabEndpoints.MaxStackSize) 
                throw new FormatException();
            return state;
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException)
        {
            throw new LabException(400, "Invalid state cookie. Start a new client session.");
        }
    }
}
