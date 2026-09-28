namespace Burmalda.Contracts;

public static class MessageNames
{
    public const string UserRegistered = nameof(UserRegistered);
    public const string ReserveBookCopy = nameof(ReserveBookCopy);
    public const string BookCopyReserved = nameof(BookCopyReserved);
    public const string BookCopyReservationFailed = nameof(BookCopyReservationFailed);
    public const string ReleaseBookCopy = nameof(ReleaseBookCopy);
    public const string BookCopyReleased = nameof(BookCopyReleased);
    public const string ReservePoints = nameof(ReservePoints);
    public const string PointsReserved = nameof(PointsReserved);
    public const string PointsReservationFailed = nameof(PointsReservationFailed);
    public const string ReleasePoints = nameof(ReleasePoints);
    public const string LoanRequested = nameof(LoanRequested);
    public const string LoanAccepted = nameof(LoanAccepted);
    public const string LoanRejected = nameof(LoanRejected);
    public const string LoanCancelled = nameof(LoanCancelled);
    public const string LoanActivated = nameof(LoanActivated);
    public const string ReturnInitiated = nameof(ReturnInitiated);
    public const string LoanCompleted = nameof(LoanCompleted);
    public const string LoanBecameOverdue = nameof(LoanBecameOverdue);
    public const string FineCreated = nameof(FineCreated);
    public const string ReturnReminderDue = nameof(ReturnReminderDue);
}

public sealed record UserRegistered(Guid UserId, string Email, string DisplayName);
public sealed record ReserveBookCopy(Guid SagaId, Guid CopyId, Guid BorrowerId, DateTimeOffset ExpiresAt);
public sealed record BookCopyReserved(Guid SagaId, Guid CopyId, Guid OwnerId, decimal LendingPrice, int MaximumLoanDays);
public sealed record BookCopyReservationFailed(Guid SagaId, Guid CopyId, string Reason);
public sealed record ReleaseBookCopy(Guid SagaId, Guid CopyId, string Reason);
public sealed record BookCopyReleased(Guid SagaId, Guid CopyId);
public sealed record ReservePoints(Guid SagaId, Guid UserId, decimal Amount, DateTimeOffset ExpiresAt);
public sealed record PointsReserved(Guid SagaId, Guid ReservationId, Guid UserId, decimal Amount);
public sealed record PointsReservationFailed(Guid SagaId, Guid UserId, decimal Amount, string Reason);
public sealed record ReleasePoints(Guid SagaId, Guid ReservationId, string Reason);
public sealed record LoanRequested(Guid LoanId, Guid SagaId, Guid CopyId, Guid BorrowerId, Guid OwnerId, decimal Price);
public sealed record LoanAccepted(Guid LoanId, Guid CopyId, Guid BorrowerId, Guid OwnerId, DateTimeOffset HandoverExpiresAt);
public sealed record LoanRejected(Guid LoanId, Guid SagaId, Guid CopyId, Guid BorrowerId, Guid OwnerId, string Reason);
public sealed record LoanCancelled(Guid LoanId, Guid SagaId, Guid CopyId, Guid BorrowerId, Guid OwnerId, string Reason);
public sealed record LoanActivated(Guid LoanId, Guid SagaId, Guid ReservationId, Guid CopyId, Guid BorrowerId, Guid OwnerId, decimal Price, DateTimeOffset DueAt);
public sealed record ReturnInitiated(Guid LoanId, Guid CopyId, Guid BorrowerId, Guid OwnerId);
public sealed record LoanCompleted(Guid LoanId, Guid CopyId, Guid BorrowerId, Guid OwnerId, decimal LenderReward, bool ReturnedOnTime);
public sealed record LoanBecameOverdue(Guid LoanId, Guid CopyId, Guid BorrowerId, Guid OwnerId, int DaysOverdue);
public sealed record FineCreated(Guid FineId, Guid LoanId, Guid UserId, decimal Amount, string Reason);
public sealed record ReturnReminderDue(Guid LoanId, Guid BorrowerId, DateTimeOffset DueAt, int DaysRemaining);
