using Burmalda.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

namespace Burmalda.Catalog.Api;

public enum BookCopyStatus { Available, Reserved, OnLoan, Unavailable }
public enum BookCondition { New, Excellent, Good, Used, Poor }

public sealed class Book
{
    public Guid Id { get; set; }
    public required string Isbn { get; set; }
    public required string Title { get; set; }
    public string Description { get; set; } = "";
    public required string AuthorsJson { get; set; }
    public required string GenresJson { get; set; }
    public string? Publisher { get; set; }
    public int? PublicationYear { get; set; }
    public string? CoverUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class BookCopy
{
    public Guid Id { get; set; }
    public Guid BookId { get; set; }
    public Guid OwnerId { get; set; }
    public BookCondition Condition { get; set; }
    public BookCopyStatus Status { get; set; }
    public decimal LendingPrice { get; set; }
    public int MaximumLoanDays { get; set; }
    public required string Location { get; set; }
    public bool IsHidden { get; set; }
    public Guid? ReservationSagaId { get; set; }
    public Guid? ReservedForUserId { get; set; }
    public DateTimeOffset? ReservationExpiresAt { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class BookSearchProjection
{
    public Guid BookId { get; set; }
    public required string Isbn { get; set; }
    public required string Title { get; set; }
    public required string AuthorsText { get; set; }
    public required string GenresText { get; set; }
    public int AvailableCopies { get; set; }
    public decimal? MinimumPrice { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Book> Books => Set<Book>();
    public DbSet<BookCopy> BookCopies => Set<BookCopy>();
    public DbSet<BookSearchProjection> SearchProjections => Set<BookSearchProjection>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Book>(entity =>
        {
            entity.ToTable("books"); entity.HasKey(x => x.Id); entity.HasIndex(x => x.Isbn).IsUnique();
            entity.Property(x => x.AuthorsJson).HasColumnType("jsonb"); entity.Property(x => x.GenresJson).HasColumnType("jsonb");
        });
        modelBuilder.Entity<BookCopy>(entity =>
        {
            entity.ToTable("book_copies"); entity.HasKey(x => x.Id); entity.HasIndex(x => x.BookId); entity.HasIndex(x => new { x.Status, x.IsHidden });
            entity.Property(x => x.LendingPrice).HasPrecision(18, 2); entity.Property(x => x.Version).IsConcurrencyToken();
        });
        modelBuilder.Entity<BookSearchProjection>(entity => { entity.ToTable("book_search_projection"); entity.HasKey(x => x.BookId); });
        modelBuilder.AddMessagingModels();
    }
}
