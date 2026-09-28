using Burmalda.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

namespace Burmalda.Reputation.Api;

public sealed class UserReputation
{
    public Guid UserId { get; set; }
    public int Score { get; set; } = 50;
    public int SuccessfulLoans { get; set; }
    public int SuccessfulReturns { get; set; }
    public int OverdueLoans { get; set; }
    public int CancelledLoans { get; set; }
    public decimal? AverageRating { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; }
}

public sealed class ReputationAdjustment
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public int Delta { get; set; }
    public required string Reason { get; set; }
    public string? IdempotencyKey { get; set; }
    public Guid? ActorId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ReputationDbContext(DbContextOptions<ReputationDbContext> options) : DbContext(options)
{
    public DbSet<UserReputation> Reputations => Set<UserReputation>();
    public DbSet<ReputationAdjustment> Adjustments => Set<ReputationAdjustment>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserReputation>(e => { e.ToTable("user_reputations"); e.HasKey(x => x.UserId); e.Property(x => x.AverageRating).HasPrecision(3, 2); e.Property(x => x.Version).IsConcurrencyToken(); });
        modelBuilder.Entity<ReputationAdjustment>(e => { e.ToTable("reputation_adjustments"); e.HasKey(x => x.Id); e.HasIndex(x => x.IdempotencyKey).IsUnique(); e.HasIndex(x => new { x.UserId, x.CreatedAt }); });
        modelBuilder.AddMessagingModels();
    }
}
