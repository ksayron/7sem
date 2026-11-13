using System.Security.Claims;
using System.Text.Json;
using Burmalda.DemoControl;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Burmalda.DemoControl.Tests;

public class ControlSafetyTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task OnlyAnAuthenticatedAdminBearerCanExemptCookiePostsFromAntiforgery(bool authenticated, bool admin, bool requiresCsrf)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(admin ? [new Claim(ClaimTypes.Role, "Admin")] : [], "Bearer"));
        var result = authenticated ? AuthenticateResult.Success(new AuthenticationTicket(principal, "Bearer")) : AuthenticateResult.Fail("invalid token");
        var services = new ServiceCollection().AddSingleton<IAuthenticationService>(new AuthenticationStub(result)).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = "POST";
        context.Request.Headers.Authorization = "Bearer invalid-or-non-admin-token";
        // The group policy may already have authenticated an ambient admin cookie.
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Admin")], "demo-cookie"));
        var csrf = new AntiforgeryStub();
        await new DemoApiAntiforgeryFilter(csrf).InvokeAsync(EndpointFilterInvocationContext.Create(context), _ => ValueTask.FromResult<object?>(null));
        Assert.Equal(requiresCsrf, csrf.Validated);
    }

    [Fact]
    public async Task EngineLimitsActorsRejectsConcurrentRunsAndKeepsEmergencyStopIdempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new PooledDbContextFactory<DemoDb>(new DbContextOptionsBuilder<DemoDb>().UseSqlite(connection).Options);
        using var metrics = new DemoMetrics();
        var store = new DemoStore(factory, metrics);
        await store.Initialize();
        var protection = new EphemeralDataProtectionProvider();
        var protector = protection.CreateProtector("Burmalda.DemoControl.Fixtures.v1");
        var session = protector.Protect(JsonSerializer.Serialize(new SessionTokens("test", "test", DateTimeOffset.UtcNow.AddHours(1))));
        await store.Write("fixtures", Enumerable.Range(0, 3).Select(i => new Fixture(i, "test@example.test", "Test", session, Guid.NewGuid(), Guid.NewGuid())).ToArray());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Demo:ServiceAccountDirectory"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) }).Build();
        using var kube = new KubeControl(configuration, store);
        using var http = new HttpClient(new BlockUntilCancelledHandler()) { BaseAddress = new Uri("http://gateway") };
        using var engine = new DemoEngine(store, metrics, new ClientFactory(http), configuration, protection, kube, NullLogger<DemoEngine>.Instance);
        await engine.StartAsync(CancellationToken.None);
        try
        {
            await Until(() => engine.Snapshot().Preparation.Ready == 3);
            await engine.Start(new RunOptions(Peak: 3, RampSeconds: 5, HoldSeconds: 30));
            await Assert.ThrowsAsync<DemoConflict>(() => engine.Start(new RunOptions(Peak: 3)));
            await Until(() => engine.Snapshot().Active == 3);
            Assert.Equal(3, engine.Snapshot().Target);
            await engine.StopRun(true);
            await engine.StopRun(false);
            await engine.StopRun(true);
            await Until(() => engine.Snapshot().Run?.State == "aborted");
            Assert.Equal(0, engine.Snapshot().Active);
            Assert.Empty(engine.Snapshot().Faults);
            await engine.StopRun(false);
            Assert.Equal("aborted", engine.Snapshot().Run?.State);
        }
        finally { await engine.StopAsync(CancellationToken.None); }
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (!condition() && DateTimeOffset.UtcNow < deadline) await Task.Delay(50);
        Assert.True(condition(), "Engine state did not settle before the deadline.");
    }
    private sealed class BlockUntilCancelledHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return new(); }
    }
    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class AntiforgeryStub : IAntiforgery
    {
        public bool Validated { get; private set; }
        public Task ValidateRequestAsync(HttpContext context) { Validated = true; return Task.CompletedTask; }
        public Task<bool> IsRequestValidAsync(HttpContext context) => throw new NotSupportedException();
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext context) => throw new NotSupportedException();
        public AntiforgeryTokenSet GetTokens(HttpContext context) => throw new NotSupportedException();
        public void SetCookieTokenAndHeader(HttpContext context) => throw new NotSupportedException();
    }
    private sealed class AuthenticationStub(AuthenticateResult result) : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(result);
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
    }
}
