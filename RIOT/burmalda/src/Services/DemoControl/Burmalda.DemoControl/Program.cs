using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Burmalda.DemoControl;
using Burmalda.DemoControl.Components;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

var builder = WebApplication.CreateBuilder(args);
var dataPath = builder.Configuration["Demo:DataPath"] ?? Path.Combine(builder.Environment.ContentRootPath, ".data");
Directory.CreateDirectory(dataPath);
builder.Services.AddDbContextFactory<DemoDb>(options => options.UseSqlite($"Data Source={Path.Combine(dataPath, "demo.db")};Default Timeout=15"));
builder.Services.AddDataProtection().SetApplicationName("Burmalda.DemoControl").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPath, "keys")));
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAntiforgery();
var validation = new TokenValidationParameters {
    ValidateIssuer = true, ValidIssuer = "burmalda-identity", ValidateAudience = true, ValidAudience = "burmalda-api",
    ValidateLifetime = true, ValidateIssuerSigningKey = true, ClockSkew = TimeSpan.FromSeconds(15),
    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:SigningKey"] ?? throw new InvalidOperationException("Jwt:SigningKey required"))),
    RoleClaimType = ClaimTypes.Role, NameClaimType = ClaimTypes.NameIdentifier };
builder.Services.AddAuthentication("demo-cookie").AddCookie("demo-cookie", options => {
    options.LoginPath = "/login"; options.AccessDeniedPath = "/login";
    options.Cookie.Name = "burmalda-demo"; options.Cookie.HttpOnly = true; options.Cookie.SameSite = SameSiteMode.Strict;
    options.ExpireTimeSpan = TimeSpan.FromMinutes(30); options.SlidingExpiration = false;
    options.Events.OnRedirectToLogin = context => { if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = 401;
        else context.Response.Redirect(context.RedirectUri); return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
}).AddJwtBearer("Bearer", options => options.TokenValidationParameters = validation);
builder.Services.AddAuthorization(options => {
    options.AddPolicy("DemoAdmin", policy => policy.AddAuthenticationSchemes("demo-cookie", "Bearer").RequireAuthenticatedUser().RequireRole("Admin"));
    options.AddPolicy("DemoUi", policy => policy.AddAuthenticationSchemes("demo-cookie").RequireAuthenticatedUser().RequireRole("Admin"));
});
var gateway = builder.Configuration["Demo:GatewayUrl"] ?? "http://gateway:8080";
builder.Services.AddHttpClient("traffic", client => { client.BaseAddress = new Uri(gateway); client.Timeout = TimeSpan.FromSeconds(15); })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromSeconds(10), MaxConnectionsPerServer = 256 });
builder.Services.AddHttpClient("login", client => { client.BaseAddress = new Uri(gateway); client.Timeout = TimeSpan.FromSeconds(15); });
builder.Services.AddHttpClient("prometheus", client => { client.BaseAddress = new Uri(builder.Configuration["Demo:PrometheusUrl"] ?? "http://prometheus:9090"); client.Timeout = TimeSpan.FromSeconds(4); });
builder.Services.AddSingleton<DemoMetrics>(); builder.Services.AddSingleton<DemoStore>(); builder.Services.AddSingleton<KubeControl>();
builder.Services.AddSingleton<DemoEngine>(); builder.Services.AddHostedService(sp => sp.GetRequiredService<DemoEngine>());
builder.Services.AddSingleton<ClusterObserver>(); builder.Services.AddHostedService(sp => sp.GetRequiredService<ClusterObserver>());
builder.Services.AddOpenTelemetry().ConfigureResource(resource => resource.AddService("demo-control", serviceInstanceId: builder.Configuration["POD_UID"] ?? Environment.MachineName)
    .AddAttributes(new Dictionary<string, object> { ["k8s.namespace.name"] = builder.Configuration["POD_NAMESPACE"] ?? "local", ["k8s.pod.name"] = builder.Configuration["HOSTNAME"] ?? Environment.MachineName }))
    .WithMetrics(metrics => {
        metrics.AddMeter("Burmalda.Demo").AddAspNetCoreInstrumentation().AddRuntimeInstrumentation()
            .AddView("burmalda_demo_request_duration", new ExplicitBucketHistogramConfiguration {
                Boundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 15] });
        if (builder.Configuration.GetValue("Telemetry:Enabled", true)) metrics.AddOtlpExporter();
    });
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
var app = builder.Build();
await app.Services.GetRequiredService<DemoStore>().Initialize();
app.Use(async (context, next) => {
    try { await next(); }
    catch (DemoConflict ex) { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { message = ex.Message }); }
    catch (AntiforgeryValidationException) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { message = "Обновите страницу и повторите действие: проверка сессии не пройдена." }); }
});
app.UseStaticFiles(); app.UseAuthentication(); app.UseAuthorization(); app.UseAntiforgery();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" })).AllowAnonymous();
app.MapGet("/login", (HttpContext context, IAntiforgery antiforgery) => {
    var token = antiforgery.GetAndStoreTokens(context);
    return Results.Content(LoginPage(token.RequestToken!, context.Request.Query.ContainsKey("error")), "text/html; charset=utf-8");
}).AllowAnonymous();
app.MapPost("/login", async (HttpContext context, IAntiforgery antiforgery, IHttpClientFactory clients) => {
    await antiforgery.ValidateRequestAsync(context);
    var form = await context.Request.ReadFormAsync();
    using var response = await clients.CreateClient("login").PostAsJsonAsync("/api/identity/login", new { email = form["email"].ToString(), password = form["password"].ToString() });
    if (!response.IsSuccessStatusCode) return Results.Redirect("/login?error=1");
    using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    var access = document.RootElement.GetProperty("accessToken").GetString()!;
    ClaimsPrincipal principal;
    try { principal = new JwtSecurityTokenHandler().ValidateToken(access, validation, out _); }
    catch (SecurityTokenException) { return Results.Redirect("/login?error=1"); }
    if (!principal.IsInRole("Admin")) return Results.Redirect("/login?error=1");
    await context.SignInAsync("demo-cookie", principal, new AuthenticationProperties { ExpiresUtc = document.RootElement.GetProperty("expiresAt").GetDateTimeOffset(), IsPersistent = false });
    return Results.Redirect("/");
}).AllowAnonymous();
app.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery) => {
    await antiforgery.ValidateRequestAsync(context); await context.SignOutAsync("demo-cookie"); return Results.Redirect("/login");
}).RequireAuthorization("DemoAdmin");
app.MapGet("/internal/faults/{service}", (string service, HttpContext context, IConfiguration config, DemoEngine engine) => {
    var expected = config["Demo:ControlKey"] ?? "";
    var actual = context.Request.Headers["X-Demo-Key"].ToString();
    if (expected.Length < 32 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual))) return Results.Unauthorized();
    return service is "catalog" or "gateway" ? Results.Ok(engine.GetFault(service)) : Results.NotFound();
}).AllowAnonymous();
var api = app.MapGroup("/api/demo").RequireAuthorization("DemoAdmin");
api.AddEndpointFilter<DemoApiAntiforgeryFilter>();
api.MapGet("/presets", () => new[] { new { id = "wave", name = "Плавная волна" }, new { id = "spike", name = "Резкий всплеск" },
    new { id = "mixed", name = "Чтение и операции" }, new { id = "recovery", name = "Восстановление под нагрузкой" } });
api.MapGet("/state", async (DemoEngine engine, ClusterObserver observer, DemoStore store) => new { engine = engine.Snapshot(), cluster = observer.Snapshot, events = await store.Events() });
api.MapGet("/settings", async (DemoStore store) => await store.Read<RunOptions>("settings") ?? new RunOptions());
api.MapPost("/prepare", async (PrepareRequest request, DemoEngine engine) => { await engine.Prepare(request.Clients); return Results.Accepted(); });
api.MapPost("/runs", async (RunOptions request, DemoEngine engine) => { await engine.Start(request); return Results.Accepted(); });
api.MapPost("/target", async (TargetRequest request, DemoEngine engine) => { await engine.SetTarget(request.Clients); return Results.Ok(); });
api.MapPost("/ramp-down", async (DemoEngine engine) => { await engine.RampDown(); return Results.Ok(); });
api.MapPost("/stop", async (StopRequest request, DemoEngine engine) => { await engine.StopRun(request.Immediate); return Results.Ok(); });
api.MapPost("/faults", async (FaultRequest request, DemoEngine engine) => { await engine.SetFault(request); return Results.Ok(); });
api.MapPost("/faults/clear", async (DemoEngine engine) => { await engine.ClearFaults(); return Results.Ok(); });
api.MapPost("/reconcile", async (DemoEngine engine) => { await engine.Reconcile(); return Results.Ok(); });
api.MapPost("/pods/{name}/delete", async (string name, DeleteRequest request, KubeControl kube) => { await kube.DeletePod(name, request.Uid); return Results.Ok(); });
api.MapGet("/runs/{id:guid}", async (Guid id, DemoStore store) => await store.Read<JsonElement?>("result:" + id) is { } value ? Results.Ok(value) : Results.NotFound());
api.MapGet("/stream", async (HttpContext context, DemoEngine engine, ClusterObserver observer) => {
    context.Response.ContentType = "text/event-stream"; context.Response.Headers.CacheControl = "no-store";
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
    do {
        await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(new { engine = engine.Snapshot(), cluster = observer.Snapshot }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    } while (await timer.WaitForNextTickAsync(context.RequestAborted));
});
app.MapRazorComponents<App>().AddInteractiveServerRenderMode().RequireAuthorization("DemoUi");
await app.RunAsync();

static string LoginPage(string token, bool error) => """
<!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Вход · Пульт Burmalda</title><link rel="stylesheet" href="/demo.css"></head>
<body><main class="login"><a class="brand" href="http://localhost:5300">Burmalda <span>Доброта</span></a><h1>Пульт демонстрации</h1><p>Войдите аккаунтом администратора Burmalda.</p>
""" + (error ? "<p class=error role=alert>Не удалось войти. Проверьте данные и роль администратора.</p>" : "") +
"<form method=post action=/login><input type=hidden name=__RequestVerificationToken value=\"" + WebUtility.HtmlEncode(token) + "\">" + """
<label for="email">Электронная почта</label><input id="email" name="email" type="email" autocomplete="username" value="admin@burmalda.local" required>
<label for="password">Пароль</label><input id="password" name="password" type="password" autocomplete="current-password" required><button type="submit">Войти в пульт</button></form><p class="muted">Отдельный сервис · Kubernetes Docker Desktop</p></main></body></html>
""";
public sealed record PrepareRequest(int Clients);
public sealed record TargetRequest(int Clients);
public sealed record StopRequest(bool Immediate = false);
public sealed record DeleteRequest(string Uid);
public partial class Program;
