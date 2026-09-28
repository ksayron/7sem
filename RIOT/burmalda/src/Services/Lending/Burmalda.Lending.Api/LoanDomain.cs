using System.Security.Cryptography;
using System.Text;

namespace Burmalda.Lending.Api;

public enum LoanStatus { Requested, Accepted, Rejected, Cancelled, HandoverPending, Active, ReturnPending, Completed, Overdue, Disputed, Lost }
public enum LoanSagaState { AwaitingCopyReservation, AwaitingPointsReservation, Completed, CompensatingCopy, Failed, TimedOut }

public sealed class DomainRuleException(string message) : Exception(message);

public sealed class Loan
{
    public Guid Id { get; set; }
    public Guid SagaId { get; set; }
    public Guid CopyId { get; set; }
    public Guid BorrowerId { get; set; }
    public Guid OwnerId { get; set; }
    public Guid ReservationId { get; set; }
    public decimal Price { get; set; }
    public int MaximumLoanDays { get; set; }
    public LoanStatus Status { get; set; }
    public string? HandoverCodeHash { get; set; }
    public DateTimeOffset? HandoverExpiresAt { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int ReminderFlags { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public string Accept(Guid actorId, DateTimeOffset now)
    {
        RequireOwner(actorId);
        RequireStatus(LoanStatus.Requested);
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        HandoverCodeHash = Hash(code); HandoverExpiresAt = now.AddHours(24); Status = LoanStatus.Accepted; Touch(now);
        return code;
    }

    public void Reject(Guid actorId, DateTimeOffset now) { RequireOwner(actorId); RequireStatus(LoanStatus.Requested); Status = LoanStatus.Rejected; Touch(now); }
    public void Cancel(Guid actorId, DateTimeOffset now) { if (actorId != BorrowerId) throw new DomainRuleException("Only the borrower can cancel this request."); RequireStatus(LoanStatus.Requested, LoanStatus.Accepted); Status = LoanStatus.Cancelled; Touch(now); }

    public void ConfirmHandover(Guid actorId, string code, DateTimeOffset now)
    {
        if (actorId != BorrowerId) throw new DomainRuleException("Only the borrower can confirm handover.");
        RequireStatus(LoanStatus.Accepted);
        if (HandoverExpiresAt <= now) throw new DomainRuleException("Handover code expired.");
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(HandoverCodeHash!), Encoding.UTF8.GetBytes(Hash(code)))) throw new DomainRuleException("Invalid handover code.");
        Status = LoanStatus.Active; ActivatedAt = now; DueAt = now.AddDays(MaximumLoanDays); HandoverCodeHash = null; Touch(now);
    }

    public void InitiateReturn(Guid actorId, DateTimeOffset now)
    {
        if (actorId != BorrowerId) throw new DomainRuleException("Only the borrower can initiate return.");
        RequireStatus(LoanStatus.Active, LoanStatus.Overdue); Status = LoanStatus.ReturnPending; Touch(now);
    }

    public bool CompleteReturn(Guid actorId, DateTimeOffset now)
    {
        RequireOwner(actorId); RequireStatus(LoanStatus.ReturnPending); var onTime = DueAt >= now; Status = LoanStatus.Completed; CompletedAt = now; Touch(now); return onTime;
    }

    public void MarkOverdue(DateTimeOffset now) { RequireStatus(LoanStatus.Active); if (DueAt >= now) throw new DomainRuleException("Loan is not overdue."); Status = LoanStatus.Overdue; Touch(now); }
    private void RequireOwner(Guid actorId) { if (actorId != OwnerId) throw new DomainRuleException("Only the copy owner can perform this transition."); }
    private void RequireStatus(params LoanStatus[] allowed) { if (!allowed.Contains(Status)) throw new DomainRuleException($"Transition is invalid from {Status}."); }
    private void Touch(DateTimeOffset now) { UpdatedAt = now; Version++; }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed class LoanRequestSaga
{
    public Guid Id { get; set; }
    public Guid CopyId { get; set; }
    public Guid BorrowerId { get; set; }
    public Guid? OwnerId { get; set; }
    public Guid? PointsReservationId { get; set; }
    public Guid? LoanId { get; set; }
    public decimal? Price { get; set; }
    public int? MaximumLoanDays { get; set; }
    public LoanSagaState State { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset Deadline { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class LoanStatusHistory
{
    public long Id { get; set; }
    public Guid LoanId { get; set; }
    public LoanStatus Status { get; set; }
    public Guid? ActorId { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
