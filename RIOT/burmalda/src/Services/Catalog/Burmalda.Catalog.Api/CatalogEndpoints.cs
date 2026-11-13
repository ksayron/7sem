using System.Text.Json;
using Burmalda.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

namespace Burmalda.Catalog.Api;

public sealed record CreateBookRequest(string Isbn, string Title, string? Description, string[] Authors, string[] Genres, string? Publisher, int? PublicationYear, string? CoverUrl);
public sealed record ListCopyRequest(Guid BookId, BookCondition Condition, decimal LendingPrice, int MaximumLoanDays, string Location);

public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var catalog = app.MapGroup("/api/catalog").RequireAuthorization();
        catalog.MapPost("/books", CreateBook);
        catalog.MapPost("/copies", ListCopy);
        catalog.MapGet("/books/search", Search);
        catalog.MapGet("/books/{id:guid}", GetBook);
        catalog.MapGet("/books/{id:guid}/copies", GetAvailableCopies);
        catalog.MapGet("/copies/mine", GetMyCopies);

        var admin = app.MapGroup("/api/admin/catalog").RequireAuthorization("ModeratorOrAdmin");
        admin.MapGet("/copies", GetAllCopies);
        admin.MapPut("/copies/{id:guid}/hidden/{hidden:bool}", SetHidden);
        admin.MapPut("/copies/{id:guid}/available", ForceAvailable);
        admin.MapGet("/messaging", MessagingStatus);
        return app;
    }

    private static async Task<IResult> CreateBook(CreateBookRequest request, CatalogDbContext db, CancellationToken ct)
    {
        if (!CatalogRequestValidation.IsValid(request))
            return Results.BadRequest(new { message = "ISBN, title, and at least one author are required." });
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await PostgresCoordination.LockTransaction(db, "catalog:isbn:" + request.Isbn.Trim(), ct);
        var existing = await db.Books.SingleOrDefaultAsync(x => x.Isbn == request.Isbn.Trim(), ct);
        if (existing is not null) return Results.Ok(new { existing.Id, existing.Isbn, existing.Title });
        var book = new Book
        {
            Id = Guid.NewGuid(), Isbn = request.Isbn.Trim(), Title = request.Title.Trim(), Description = request.Description?.Trim() ?? "",
            AuthorsJson = JsonSerializer.Serialize(request.Authors), GenresJson = JsonSerializer.Serialize(request.Genres), Publisher = request.Publisher,
            PublicationYear = request.PublicationYear, CoverUrl = request.CoverUrl, CreatedAt = DateTimeOffset.UtcNow
        };
        db.Books.Add(book);
        db.SearchProjections.Add(new BookSearchProjection
        {
            BookId = book.Id, Isbn = book.Isbn, Title = book.Title, AuthorsText = string.Join(", ", request.Authors),
            GenresText = string.Join(", ", request.Genres), AvailableCopies = 0, UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Created($"/api/catalog/books/{book.Id}", new { book.Id });
    }

    private static async Task<IResult> ListCopy(ListCopyRequest request, HttpContext context, CatalogDbContext db, CancellationToken ct)
    {
        if (!CatalogRequestValidation.IsValid(request)) return Results.BadRequest(new { message = "Invalid lending terms." });
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await CatalogProjection.LockBook(db, request.BookId, ct);
        if (!await db.Books.AnyAsync(x => x.Id == request.BookId, ct)) return Results.NotFound(new { message = "Book not found." });
        var copy = new BookCopy
        {
            Id = Guid.NewGuid(), BookId = request.BookId, OwnerId = context.User.RequireUserId(), Condition = request.Condition,
            Status = BookCopyStatus.Available, LendingPrice = request.LendingPrice, MaximumLoanDays = request.MaximumLoanDays,
            Location = request.Location.Trim(), Version = 1, CreatedAt = DateTimeOffset.UtcNow
        };
        db.BookCopies.Add(copy);
        await CatalogProjection.Refresh(db, copy.BookId, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Created($"/api/catalog/books/{request.BookId}/copies", new { copy.Id });
    }

    private static async Task<IResult> Search(string? q, CatalogDbContext db, int skip = 0, int take = 50, CancellationToken ct = default)
    {
        var query = db.SearchProjections.AsNoTracking().Where(x => x.AvailableCopies > 0);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{q.Trim()}%";
            query = query.Where(x => EF.Functions.ILike(x.Title, pattern) || EF.Functions.ILike(x.AuthorsText, pattern) || EF.Functions.ILike(x.GenresText, pattern) || EF.Functions.ILike(x.Isbn, pattern));
        }
        return Results.Ok(await query.OrderBy(x => x.Title).Skip(skip).Take(Math.Clamp(take, 1, 100)).ToListAsync(ct));
    }

    private static async Task<IResult> GetBook(Guid id, CatalogDbContext db, CancellationToken ct)
    {
        var book = await db.Books.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return book is null ? Results.NotFound() : Results.Ok(book);
    }

    private static async Task<IResult> GetAvailableCopies(Guid id, CatalogDbContext db, CancellationToken ct) => Results.Ok(await db.BookCopies.AsNoTracking()
        .Where(x => x.BookId == id && x.Status == BookCopyStatus.Available && !x.IsHidden)
        .Select(x => new { x.Id, x.OwnerId, x.Condition, x.LendingPrice, x.MaximumLoanDays, x.Location, x.Version }).ToListAsync(ct));

    private static async Task<IResult> GetMyCopies(HttpContext context, CatalogDbContext db, CancellationToken ct) =>
        Results.Ok(await db.BookCopies.AsNoTracking().Where(x => x.OwnerId == context.User.RequireUserId()).OrderByDescending(x => x.CreatedAt).ToListAsync(ct));

    private static async Task<IResult> GetAllCopies(CatalogDbContext db, BookCopyStatus? status, int take = 100, CancellationToken ct = default) =>
        Results.Ok(await db.BookCopies.AsNoTracking().Where(x => status == null || x.Status == status).OrderByDescending(x => x.CreatedAt).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct));

    private static async Task<IResult> SetHidden(Guid id, bool hidden, CatalogDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await CatalogProjection.LockCopy(db, id, ct);
        var copy = await db.BookCopies.FindAsync([id], ct); if (copy is null) return Results.NotFound();
        copy.IsHidden = hidden; copy.Version++; await CatalogProjection.Refresh(db, copy.BookId, ct); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return Results.NoContent();
    }

    private static async Task<IResult> ForceAvailable(Guid id, CatalogDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await CatalogProjection.LockCopy(db, id, ct);
        var copy = await db.BookCopies.FindAsync([id], ct); if (copy is null) return Results.NotFound();
        copy.Status = BookCopyStatus.Available; copy.ReservationSagaId = null; copy.ReservedForUserId = null; copy.ReservationExpiresAt = null; copy.Version++;
        await CatalogProjection.Refresh(db, copy.BookId, ct); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return Results.NoContent();
    }

    private static async Task<IResult> MessagingStatus(CatalogDbContext db, CancellationToken ct) => Results.Ok(new
    {
        PendingOutbox = await db.Set<OutboxMessage>().CountAsync(x => x.PublishedAt == null, ct),
        PublishedOutbox = await db.Set<OutboxMessage>().CountAsync(x => x.PublishedAt != null, ct),
        Inbox = await db.Set<InboxMessage>().CountAsync(ct)
    });
}

public static class CatalogProjection
{
    public static Task LockBook(CatalogDbContext db, Guid bookId, CancellationToken ct) =>
        PostgresCoordination.LockTransaction(db, "catalog:book:" + bookId, ct);

    public static async Task LockCopy(CatalogDbContext db, Guid copyId, CancellationToken ct)
    {
        var bookId = await db.BookCopies.AsNoTracking().Where(x => x.Id == copyId).Select(x => (Guid?)x.BookId).SingleOrDefaultAsync(ct);
        if (bookId is not null) await LockBook(db, bookId.Value, ct);
    }

    public static async Task Refresh(CatalogDbContext db, Guid bookId, CancellationToken ct)
    {
        // Flush the new copy status before querying the projection, within the caller's transaction.
        await db.SaveChangesAsync(ct);
        var projection = await db.SearchProjections.SingleAsync(x => x.BookId == bookId, ct);
        var available = db.BookCopies.Where(x => x.BookId == bookId && x.Status == BookCopyStatus.Available && !x.IsHidden);
        projection.AvailableCopies = await available.CountAsync(ct);
        projection.MinimumPrice = await available.Select(x => (decimal?)x.LendingPrice).MinAsync(ct);
        projection.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
