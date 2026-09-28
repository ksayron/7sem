# Initial event and command catalog

Commands express intent to one logical receiver; events are past-tense facts and
may have multiple consumers.

| Contract | Kind | Producer | Consumer(s) | Purpose |
| --- | --- | --- | --- | --- |
| `ReserveBookCopy` | command | Lending Saga | Catalog | Atomically reserve the requested copy |
| `ReleaseBookCopy` | command | Lending Saga/Loan | Catalog | Compensate a failed/cancelled workflow |
| `ReservePoints` | command | Lending Saga | Economy | Reserve the advertised BD price |
| `ReleasePoints` | command | Lending Saga/Loan | Economy | Release uncaptured BD |
| `UserRegistered` | event | Identity | Economy, Reputation, Notifications | Create wallet/trust projections and welcome notice |
| `BookCopyReserved` | event | Catalog | Lending | Continue Saga with owner, price, and term |
| `BookCopyReservationFailed` | event | Catalog | Lending | Fail Saga before point reservation |
| `BookCopyReleased` | event | Catalog | Lending | Finish copy compensation |
| `PointsReserved` | event | Economy | Lending | Create the public Loan request |
| `PointsReservationFailed` | event | Economy | Lending | Compensate the copy reservation |
| `LoanRequested` | event | Lending | Notifications | Notify owner and borrower |
| `LoanAccepted` | event | Lending | Notifications | Tell borrower to arrange handover |
| `LoanRejected` | event | Lending | Notifications | Report rejection; commands do compensation |
| `LoanCancelled` | event | Lending | Reputation, Notifications | Update trust and notify; commands do compensation |
| `LoanActivated` | event | Lending | Catalog, Economy, Notifications | Mark OnLoan, capture BD, notify with due date |
| `ReturnInitiated` | event | Lending | Notifications | Ask owner to confirm physical return |
| `LoanCompleted` | event | Lending | Catalog, Economy, Reputation, Notifications | Release copy, reward, update trust, notify |
| `LoanBecameOverdue` | event | Lending | Economy, Reputation, Notifications | Create fine, reduce trust, warn participants |
| `FineCreated` | event | Economy | Notifications | Notify borrower of monetary consequence |
| `ReturnReminderDue` | event | Lending job | Notifications | Persist 3-day, 1-day, or due-day reminder |

Domain events are kept inside the owning aggregate/application transaction. The
initial implementation promotes only facts needed by another service to the
integration catalog. Future ratings/disputes should add versioned contracts,
not expose domain entities.
