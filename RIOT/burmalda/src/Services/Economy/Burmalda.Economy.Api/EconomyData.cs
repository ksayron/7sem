using Burmalda.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

namespace Burmalda.Economy.Api;

public enum BalanceTransactionType { WelcomeBonus, BookListedBonus, LoanPayment, SuccessfulLendingReward, OnTimeReturnBonus, LateFine, Compensation, CommunityReward, AdministrativeAdjustment }
public enum ReservationStatus { Active, Captured, Released, Expired }
public enum FineStatus { Outstanding, Paid, Waived }

public sealed class Wallet { public Guid UserId { get; set; }
    public decimal Balance { get; set; } public DateTimeOffset UpdatedAt { get; set; } public long Version { get; set; } }
public sealed class BalanceTransaction
{
    public Guid Id { get; set; } public Guid UserId { get; set; } public decimal Amount { get; set; } public BalanceTransactionType Type { get; set; }
    public Guid? LoanId { get; set; } public required string Description { get; set; } public DateTimeOffset CreatedAt { get; set; } public required string IdempotencyKey { get; set; }
}
public sealed class PointsReservation
{
    public Guid Id { get; set; } public Guid SagaId { get; set; } public Guid UserId { get; set; } public decimal Amount { get; set; }
    public ReservationStatus Status { get; set; } public DateTimeOffset ExpiresAt { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; }
}
public sealed class Fine
{
    public Guid Id { get; set; } public Guid LoanId { get; set; } public Guid UserId { get; set; } public decimal Amount { get; set; }
    public required string Reason { get; set; } public FineStatus Status { get; set; } public DateTimeOffset CreatedAt { get; set; }
}

public sealed class EconomyDbContext(DbContextOptions<EconomyDbContext> options) : DbContext(options)
{
    public DbSet<Wallet> Wallets => Set<Wallet>(); public DbSet<BalanceTransaction> Transactions => Set<BalanceTransaction>();
    public DbSet<PointsReservation> Reservations => Set<PointsReservation>(); public DbSet<Fine> Fines => Set<Fine>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Wallet>(e => { e.ToTable("wallets"); e.HasKey(x => x.UserId); e.Property(x => x.Balance).HasPrecision(18, 2); e.Property(x => x.Version).IsConcurrencyToken(); });
        modelBuilder.Entity<BalanceTransaction>(e => { e.ToTable("balance_transactions"); e.HasKey(x => x.Id); e.HasIndex(x => x.IdempotencyKey).IsUnique(); e.HasIndex(x => new { x.UserId, x.CreatedAt }); e.Property(x => x.Amount).HasPrecision(18, 2); });
        modelBuilder.Entity<PointsReservation>(e => { e.ToTable("point_reservations"); e.HasKey(x => x.Id); e.HasIndex(x => x.SagaId).IsUnique(); e.HasIndex(x => new { x.UserId, x.Status }); e.Property(x => x.Amount).HasPrecision(18, 2); });
        modelBuilder.Entity<Fine>(e => { e.ToTable("fines"); e.HasKey(x => x.Id); e.HasIndex(x => x.LoanId).IsUnique(); e.Property(x => x.Amount).HasPrecision(18, 2); });
        modelBuilder.AddMessagingModels();
    }
}
