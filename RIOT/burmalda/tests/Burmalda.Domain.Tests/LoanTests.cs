using Burmalda.Lending.Api;

namespace Burmalda.Domain.Tests;

public sealed class LoanTests
{
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _borrower = Guid.NewGuid();

    [Fact]
    public void Owner_can_accept_and_borrower_can_activate_with_code()
    {
        var now = DateTimeOffset.UtcNow;
        var loan = CreateLoan();
        var code = loan.Accept(_owner, now);
        loan.ConfirmHandover(_borrower, code, now.AddMinutes(5));
        Assert.Equal(LoanStatus.Active, loan.Status);
        Assert.Equal(now.AddMinutes(5).AddDays(14), loan.DueAt);
    }

    [Fact]
    public void Stranger_cannot_accept_request()
    {
        var loan = CreateLoan();
        Assert.Throws<DomainRuleException>(() => loan.Accept(Guid.NewGuid(), DateTimeOffset.UtcNow));
        Assert.Equal(LoanStatus.Requested, loan.Status);
    }

    [Fact]
    public void Invalid_handover_code_does_not_activate_loan()
    {
        var loan = CreateLoan();
        loan.Accept(_owner, DateTimeOffset.UtcNow);
        Assert.Throws<DomainRuleException>(() => loan.ConfirmHandover(_borrower, "000000", DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.Equal(LoanStatus.Accepted, loan.Status);
    }

    [Fact]
    public void Only_active_due_loan_becomes_overdue()
    {
        var now = DateTimeOffset.UtcNow;
        var loan = CreateLoan();
        loan.MaximumLoanDays = 1;
        var code = loan.Accept(_owner, now.AddDays(-3));
        loan.ConfirmHandover(_borrower, code, now.AddDays(-2.5));
        loan.MarkOverdue(now);
        Assert.Equal(LoanStatus.Overdue, loan.Status);
    }

    [Fact]
    public void Return_requires_borrower_then_owner()
    {
        var now = DateTimeOffset.UtcNow;
        var loan = CreateLoan();
        var code = loan.Accept(_owner, now);
        loan.ConfirmHandover(_borrower, code, now.AddMinutes(1));
        loan.InitiateReturn(_borrower, now.AddDays(10));
        var onTime = loan.CompleteReturn(_owner, now.AddDays(10));
        Assert.True(onTime);
        Assert.Equal(LoanStatus.Completed, loan.Status);
    }

    private Loan CreateLoan() => new()
    {
        Id = Guid.NewGuid(), SagaId = Guid.NewGuid(), CopyId = Guid.NewGuid(), BorrowerId = _borrower, OwnerId = _owner,
        ReservationId = Guid.NewGuid(), Price = 10, MaximumLoanDays = 14, Status = LoanStatus.Requested,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Version = 1
    };
}
