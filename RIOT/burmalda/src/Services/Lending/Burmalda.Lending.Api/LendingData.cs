using Burmalda.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

namespace Burmalda.Lending.Api;

public sealed class LendingDbContext(DbContextOptions<LendingDbContext> options) : DbContext(options)
{
    public DbSet<Loan> Loans => Set<Loan>();
    public DbSet<LoanRequestSaga> Sagas => Set<LoanRequestSaga>();
    public DbSet<LoanStatusHistory> StatusHistory => Set<LoanStatusHistory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Loan>(entity =>
        {
            entity.ToTable("loans"); entity.HasKey(x => x.Id); entity.HasIndex(x => x.SagaId).IsUnique(); entity.HasIndex(x => x.CopyId);
            entity.HasIndex(x => new { x.BorrowerId, x.Status }); entity.HasIndex(x => new { x.OwnerId, x.Status });
            entity.Property(x => x.Price).HasPrecision(18, 2); entity.Property(x => x.Version).IsConcurrencyToken();
        });
        modelBuilder.Entity<LoanRequestSaga>(entity =>
        {
            entity.ToTable("loan_request_sagas"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.CopyId, x.State }); entity.Property(x => x.Price).HasPrecision(18, 2);
        });
        modelBuilder.Entity<LoanStatusHistory>(entity => { entity.ToTable("loan_status_history"); entity.HasKey(x => x.Id); entity.HasIndex(x => x.LoanId); });
        modelBuilder.AddMessagingModels();
    }
}

public static class LoanHistory
{
    public static void Add(LendingDbContext db, Loan loan, Guid? actorId, string reason) => db.StatusHistory.Add(new LoanStatusHistory
    {
        LoanId = loan.Id, Status = loan.Status, ActorId = actorId, Reason = reason, OccurredAt = DateTimeOffset.UtcNow
    });
}
