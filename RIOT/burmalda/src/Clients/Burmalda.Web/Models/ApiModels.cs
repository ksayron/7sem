using System.ComponentModel.DataAnnotations;

namespace Burmalda.Web.Models;

public sealed record UserSession(string AccessToken, DateTimeOffset ExpiresAt, string RefreshToken, Guid Id, string DisplayName, string Role);

public sealed class LoginForm
{
    [Required, EmailAddress] public string Email { get; set; } = "";
    [Required] public string Password { get; set; } = "";
}

public sealed class RegisterForm
{
    [Required, StringLength(80, MinimumLength = 2)] public string DisplayName { get; set; } = "";
    [Required, EmailAddress] public string Email { get; set; } = "";
    [Required, MinLength(8)] public string Password { get; set; } = "";
}

public sealed record BookSearchItem(Guid BookId, string Isbn, string Title, string AuthorsText, string GenresText, int AvailableCopies, decimal? MinimumPrice, DateTimeOffset UpdatedAt);
public sealed record BookCopyItem(Guid Id, Guid OwnerId, int Condition, decimal LendingPrice, int MaximumLoanDays, string Location, long Version);
public sealed record OwnedCopy(Guid Id, Guid BookId, Guid OwnerId, int Condition, int Status, decimal LendingPrice, int MaximumLoanDays, string Location, bool IsHidden, long Version, DateTimeOffset CreatedAt);
public sealed record CreatedId(Guid Id);
public sealed record CreatedBook(Guid Id);
public sealed record LoanRequestReceipt(Guid Id, int State, DateTimeOffset Deadline);
public sealed record LoanSagaSnapshot(Guid Id, int State, Guid CopyId, Guid? PointsReservationId,
    Guid? LoanId, string? FailureReason, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset Deadline);

public sealed record LoanItem(
    Guid Id, Guid SagaId, Guid CopyId, Guid BorrowerId, Guid OwnerId, Guid ReservationId,
    decimal Price, int MaximumLoanDays, int Status, DateTimeOffset? HandoverExpiresAt,
    DateTimeOffset? ActivatedAt, DateTimeOffset? DueAt, DateTimeOffset? CompletedAt,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record WalletSummary(Guid UserId, decimal Balance, decimal Reserved, decimal Available, DateTimeOffset UpdatedAt);
public sealed record TransactionItem(Guid Id, Guid UserId, decimal Amount, int Type, Guid? LoanId, string Description, DateTimeOffset CreatedAt);
public sealed record ReputationSummary(Guid UserId, int Score, int SuccessfulLoans, int SuccessfulReturns, int OverdueLoans, int CancelledLoans, decimal? AverageRating, DateTimeOffset UpdatedAt);
public sealed record NotificationItem(Guid Id, Guid UserId, string Type, string Title, string Body, string? DataJson, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);
public sealed record CountResult(int Count);
public sealed record ServiceHealth(bool Healthy, int? Status, string? Error);
public sealed record SystemHealth(DateTimeOffset CheckedAt, Dictionary<string, ServiceHealth> Services);

public sealed class CreateBookForm
{
    [Required] public string Isbn { get; set; } = "";
    [Required] public string Title { get; set; } = "";
    [Required] public string Authors { get; set; } = "";
    public string Genres { get; set; } = "";
    public string Description { get; set; } = "";
    public string Publisher { get; set; } = "";
    [Range(1400, 2100)] public int? PublicationYear { get; set; }
}

public sealed class ListCopyForm
{
    [Required] public Guid? BookId { get; set; }
    [Range(0, 4)] public int Condition { get; set; } = 2;
    [Range(0, 10000)] public decimal LendingPrice { get; set; } = 8;
    [Range(1, 90)] public int MaximumLoanDays { get; set; } = 14;
    [Required] public string Location { get; set; } = "Minsk";
}
