using Burmalda.Identity.Api;
using Burmalda.ServiceDefaults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Services.AddBurmaldaDefaults(builder.Configuration, "identity-service");
builder.Services.AddDbContext<IdentityDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("Database")));
builder.Services.AddScoped<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddHostedService<OutboxPublisher<IdentityDbContext>>();

var app = builder.Build();
app.UseBurmaldaDefaults();
app.MapIdentityEndpoints();
await app.EnsureDatabaseAsync<IdentityDbContext>();
await SeedAdmin(app);
await app.RunAsync();

static async Task SeedAdmin(WebApplication app)
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
    var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    var email = configuration["AdminSeed:Email"];
    var password = configuration["AdminSeed:Password"];
    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)) return;
    var normalized = email.ToUpperInvariant();
    if (await db.Users.AnyAsync(x => x.NormalizedEmail == normalized)) return;
    var user = new AppUser { Id = Guid.NewGuid(), Email = email, NormalizedEmail = normalized, DisplayName = "Administrator", PasswordHash = "", Role = "Admin", CreatedAt = DateTimeOffset.UtcNow };
    user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(user, password);
    db.Users.Add(user);
    await db.SaveChangesAsync();
}

public partial class Program;
