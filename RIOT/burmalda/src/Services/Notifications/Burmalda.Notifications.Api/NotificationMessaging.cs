using Burmalda.Contracts;
using Burmalda.ServiceDefaults;

namespace Burmalda.Notifications.Api;

public sealed class NotificationQueue : IMessageQueueDefinition
{
    public string QueueName => "notifications.loan-events";
    public IReadOnlyCollection<QueueBinding> Bindings =>
    [
        new(Outbox.EventsExchange, Routing.Event<UserRegistered>()), new(Outbox.EventsExchange, Routing.Event<LoanRequested>()),
        new(Outbox.EventsExchange, Routing.Event<LoanAccepted>()), new(Outbox.EventsExchange, Routing.Event<LoanRejected>()),
        new(Outbox.EventsExchange, Routing.Event<LoanCancelled>()), new(Outbox.EventsExchange, Routing.Event<LoanActivated>()),
        new(Outbox.EventsExchange, Routing.Event<ReturnInitiated>()), new(Outbox.EventsExchange, Routing.Event<LoanCompleted>()),
        new(Outbox.EventsExchange, Routing.Event<LoanBecameOverdue>()), new(Outbox.EventsExchange, Routing.Event<FineCreated>()),
        new(Outbox.EventsExchange, Routing.Event<ReturnReminderDue>())
    ];
}

public sealed class NotificationMessageHandler(NotificationDbContext db, string messageType) : IIntegrationMessageHandler
{
    public string MessageType => messageType;
    public Task Handle(MessageEnvelope envelope, CancellationToken ct)
    {
        switch (envelope.Type)
        {
            case MessageNames.UserRegistered: { var e = envelope.DeserializePayload<UserRegistered>(); Add(e.UserId, "Welcome", "Welcome to Burmalda Доброта", "Your 30 BD welcome bonus is being prepared.", e); break; }
            case MessageNames.LoanRequested: { var e = envelope.DeserializePayload<LoanRequested>(); Add(e.OwnerId, "LoanRequested", "New loan request", "A reader requested one of your books.", e); Add(e.BorrowerId, "LoanRequested", "Request submitted", "The copy and BD were reserved.", e); break; }
            case MessageNames.LoanAccepted: { var e = envelope.DeserializePayload<LoanAccepted>(); Add(e.BorrowerId, "LoanAccepted", "Loan accepted", "Arrange handover with the owner.", e); break; }
            case MessageNames.LoanRejected: { var e = envelope.DeserializePayload<LoanRejected>(); Add(e.BorrowerId, "LoanRejected", "Loan rejected", e.Reason, e); break; }
            case MessageNames.LoanCancelled: { var e = envelope.DeserializePayload<LoanCancelled>(); Add(e.OwnerId, "LoanCancelled", "Loan cancelled", e.Reason, e); break; }
            case MessageNames.LoanActivated: { var e = envelope.DeserializePayload<LoanActivated>(); Add(e.BorrowerId, "LoanActivated", "Loan active", $"Return the book by {e.DueAt:u}.", e); Add(e.OwnerId, "LoanActivated", "Book handed over", "The loan is now active.", e); break; }
            case MessageNames.ReturnInitiated: { var e = envelope.DeserializePayload<ReturnInitiated>(); Add(e.OwnerId, "ReturnInitiated", "Return pending", "The borrower is ready to return your book.", e); break; }
            case MessageNames.LoanCompleted: { var e = envelope.DeserializePayload<LoanCompleted>(); Add(e.BorrowerId, "LoanCompleted", "Return confirmed", "The loan is complete.", e); Add(e.OwnerId, "LoanCompleted", "Loan complete", "Your lending reward was issued.", e); break; }
            case MessageNames.LoanBecameOverdue: { var e = envelope.DeserializePayload<LoanBecameOverdue>(); Add(e.BorrowerId, "LoanOverdue", "Book overdue", "Return the book as soon as possible. A fine may be applied.", e); Add(e.OwnerId, "LoanOverdue", "Loan overdue", "Your book has not been returned on time.", e); break; }
            case MessageNames.FineCreated: { var e = envelope.DeserializePayload<FineCreated>(); Add(e.UserId, "FineCreated", "Overdue fine", $"A fine of {e.Amount} BD was applied: {e.Reason}", e); break; }
            case MessageNames.ReturnReminderDue: { var e = envelope.DeserializePayload<ReturnReminderDue>(); Add(e.BorrowerId, "ReturnReminder", "Return reminder", e.DaysRemaining == 0 ? "Your book is due today." : $"Your book is due in {e.DaysRemaining} day(s).", e); break; }
        }
        return Task.CompletedTask;
    }
    private void Add(Guid userId, string type, string title, string body, object data) => db.Notifications.Add(NotificationEndpoints.New(userId, type, title, body, data));
}
