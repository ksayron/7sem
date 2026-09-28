# CreateLoanRequest Saga

Lending owns the process manager because the desired outcome is a Lending-owned
Loan. PostgreSQL transactions cannot atomically update Catalog and Economy, so
the Saga records progress and uses compensating commands.

## State and correlation

The HTTP request creates a unique `SagaId`; it is both message correlation id
and the reservation business key. Every envelope also has its own `MessageId`
and optional `CausationId`. Catalog and Economy enforce one reservation per Saga.

| State | Incoming fact | Action | Next state |
| --- | --- | --- | --- |
| `AwaitingCopyReservation` | created | send `ReserveBookCopy` | same |
| `AwaitingCopyReservation` | `BookCopyReserved` | store terms; send `ReservePoints` | `AwaitingPointsReservation` |
| `AwaitingCopyReservation` | `BookCopyReservationFailed` | record reason | `Failed` |
| `AwaitingPointsReservation` | `PointsReserved` | create Loan + `LoanRequested` | `Completed` |
| `AwaitingPointsReservation` | `PointsReservationFailed` | send `ReleaseBookCopy` | `CompensatingCopy` |
| `CompensatingCopy` | `BookCopyReleased` | finish compensation | `Failed` |
| active state | deadline exceeded | release any acquired resource | `TimedOut` |

Messages received after terminal state are ignored or compensated. Inbox rows
make redelivery harmless; unique Saga indexes protect against semantically
duplicated commands with different message ids.

## Failure paths

```text
Reserve copy fails -> Failed

Reserve copy succeeds -> reserve points fails
                      -> ReleaseBookCopy
                      -> Failed

Timeout after copy -> ReleaseBookCopy -> TimedOut
Late PointsReserved -> ReleasePoints
```

Saga timeout is two minutes for request creation. Accepted loans have a separate
24-hour handover expiry job that releases both resources and emits
`LoanCancelled`. Operations can inspect and explicitly time out stuck Sagas.

The complexity is durable intermediate state, delayed consistency, and
compensation code. It is justified because neither a local transaction nor a
synchronous HTTP chain can guarantee atomicity across the two owners.
