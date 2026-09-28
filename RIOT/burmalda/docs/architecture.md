# Architecture analysis and domain model

## Boundaries

The proposed six contexts are retained because each has a distinct consistency
boundary and gives the academic system meaningful distributed behavior without
introducing a service for every entity.

| Context | Owns | Does not own |
| --- | --- | --- |
| Identity | credentials, access/refresh tokens, roles, account state | wallet, reputation, lending profile |
| Catalog | bibliographic books, physical copies, availability, lending terms, search projection | loan history or payments |
| Lending | Loan aggregate, state history, handover, return, loan-request Saga | copy rows or BD balances |
| Economy | wallets, reservations, append-only ledger, fines | deciding whether a loan is overdue |
| Reputation | trust projection and adjustment history | authentication or spendable currency |
| Notifications | persisted user notifications | email/push delivery infrastructure |

Fines cross a natural boundary. Lending determines the violation and emits
`LoanBecameOverdue`; Economy owns the monetary rule, ledger entry, and `Fine`.
This prevents Lending from becoming coupled to pricing policy.

Authors and genres remain Catalog-owned value data for now. Splitting them into
services would add calls and failure modes without an independent lifecycle.
Handover and return remain inside the Loan aggregate; separate services would
break its transition invariants. There is no generic "admin service": admin
commands execute in the owning bounded context. The gateway only aggregates
readiness, which is an operational concern rather than domain logic.

## Domain model

### Catalog

- `Book` is bibliographic identity (`ISBN`, title, authors, genres, publisher).
- `BookCopy` is an owner-controlled physical asset with condition, price,
  maximum duration, location, status, reservation owner, expiry, and optimistic
  version.
- `BookSearchProjection` is a replaceable query model with available-copy count
  and minimum price.
- Reservation uses one atomic conditional database update. Two concurrent
  requests cannot both change `Available` to `Reserved`.

Copy lifecycle:

```text
Available -> Reserved -> OnLoan -> Available
                |                       ^
                +---- compensation -----+
Available <-> Unavailable
```

### Lending

`Loan` is the consistency boundary for business transitions. It contains the
participants, copy, point reservation, terms, handover hash/expiry, due date,
status, and optimistic version. Methods reject transitions performed by the
wrong actor or from the wrong state. `LoanStatusHistory` is append-only audit.

```text
Requested -> Accepted -> Active -> ReturnPending -> Completed
    |           |          |
    v           v          v
Rejected     Cancelled   Overdue -> ReturnPending
```

`LoanRequestSaga` is not the Loan aggregate. It is a process manager that owns
distributed progress, deadline, reservation ids, failure reason, and
compensation state. A public Loan is created only after both reservations exist.

### Economy

`Wallet.Balance` is a projection for fast checks. Financial truth is the
append-only `BalanceTransaction` ledger. Every business entry has a unique
idempotency key. `PointsReservation` separates available and reserved balance;
activation captures it into a debit. `Fine` records the policy result and links
it to one Loan.

Available balance is:

```text
wallet projection - active reservations
```

Reservation checks lock the wallet row, so concurrent spending decisions are
serialized inside Economy.

### Reputation

`UserReputation` is an asynchronous projection with score and counters.
`ReputationAdjustment` records both event-derived changes and administrator
changes. Currency and reputation never share a table or transaction.

## CQRS

HTTP mutations and message handlers are command paths: they load the owner
aggregate, enforce rules, write state, and append outbox records. Queries use
`AsNoTracking` projections and never mutate state. Catalog search reads
`BookSearchProjection` rather than reconstructing availability from the write
model on every request. The implementation uses explicit handlers rather than a
mediator library so the separation remains visible without framework ceremony.

## Project structure

```text
src/
  BuildingBlocks/
    Burmalda.Contracts/        versioned integration DTOs only
    Burmalda.ServiceDefaults/  messaging, outbox/inbox, auth, telemetry
  Gateway/Burmalda.Gateway/    YARP routes, correlation, rate limit, health
  Services/
    Identity/*                 identity API and identity database adapter
    Catalog/*                  catalog commands, queries, projection, consumers
    Lending/*                  Loan domain, Saga, jobs, consumers
    Economy/*                  ledger, reservations, fine policy, consumers
    Reputation/*               trust projection and consumers
    Notifications/*            notification projection and consumers
tests/Burmalda.Domain.Tests/   transition and architecture tests
```

Services reference only the small contracts/defaults packages, never another
service assembly. Domain models and DbContexts are not shared.

## Data ownership

| Database | Main tables | Foreign information and how it arrives |
| --- | --- | --- |
| `identity_db` | users, refresh_tokens, outbox | none; emits `UserRegistered` |
| `catalog_db` | books, book_copies, book_search_projection, outbox, inbox | borrower and Saga ids arrive in reservation commands; loan state arrives by events |
| `lending_db` | loans, loan_status_history, loan_request_sagas, Hangfire, outbox, inbox | owner/price/term arrive in `BookCopyReserved`; reservation id arrives in `PointsReserved` |
| `economy_db` | wallets, balance_transactions, point_reservations, fines, outbox, inbox | user creation and loan facts arrive as events/commands |
| `reputation_db` | user_reputations, reputation_adjustments, outbox, inbox | loan outcomes arrive as events |
| `notification_db` | notifications, outbox, inbox | recipients and display facts arrive in events |

No database login has permission to another service database. Data needed from
another context is included in an integration contract or obtained through its
public API for a genuinely synchronous query.

## Admin controls and monitoring

The owning services expose role-gated controls:

- Identity: list users, change roles, activate/deactivate accounts;
- Catalog: inspect/hide copies and force a copy available after investigation;
- Lending: inspect loans/Sagas, force timeout, resolve disputes;
- Economy: inspect wallets/reservations/fines and append adjustments;
- Reputation: inspect and append moderated score adjustments;
- Notifications: inspect recent records and create targeted broadcasts.

Every service exposes liveness/readiness and messaging counters. Gateway
readiness aggregation, JSON logs, correlation ids, OpenTelemetry HTTP/EF/message
spans, Jaeger, RabbitMQ management, Hangfire dashboard, outbox failure details,
and DLQs provide the server-side monitoring surface. Destructive fixes are
explicit commands and retain audit/ledger records; administrators never edit
foreign tables.
