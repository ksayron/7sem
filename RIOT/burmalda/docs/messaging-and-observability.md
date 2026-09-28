# Messaging, reliability, and observability

## Envelope

```json
{
  "messageId": "uuid",
  "correlationId": "uuid",
  "causationId": "uuid-or-null",
  "type": "LoanCompleted",
  "occurredAt": "2026-09-17T12:00:00Z",
  "version": 1,
  "payload": {},
  "traceParent": "00-...",
  "traceState": null
}
```

The outbox stores the entire immutable envelope. Trace context, correlation, and
causation survive asynchronous hops.

## Topology

- durable topic exchange `burmalda.events` for facts;
- durable topic exchange `burmalda.commands` for single-receiver intent;
- one durable queue per consuming service/workflow;
- `<queue>.retry` queues with a five-second TTL;
- `<queue>.dead` poison queues bound through `burmalda.dead`;
- persistent messages and publisher reconnect/retry.

Consumers reject failed delivery into the retry queue. The message returns to
the main queue after TTL; after three cycles it is copied to the service DLQ and
acknowledged from the main queue. DLQs are inspected through RabbitMQ management;
replay is a deliberate operator action after the underlying defect is fixed.

## Transactional outbox

`outbox_messages` contains `Id`, exchange, routing key, type, serialized payload,
occurrence/publish timestamps, retry count, and last error. Domain changes and
outbox inserts share one database transaction. A lightweight hosted worker is
used rather than Hangfire because delivery polling is infrastructure plumbing,
high-frequency, and does not benefit from a business-job dashboard. Hangfire is
reserved for overdue/expiry/reminder workflows.

## Inbox and idempotency

`inbox_messages` is keyed by `(MessageId, Consumer)`. Handler changes, any new
outbox rows, and the inbox marker commit together before ACK. Economy also uses
unique business idempotency keys such as `late-fine:{loanId}` because different
messages can express the same financial intent.

## Tracing and logs

OpenTelemetry instruments ASP.NET Core, outbound HTTP, EF Core, runtime metrics,
and custom message producer/consumer activities. OTLP is sent to the collector
and Jaeger. JSON console logs include structured properties. The gateway creates
or forwards `X-Correlation-ID`, while W3C trace context follows both HTTP and the
stored message envelope.

The local health surface includes `/health/live`, `/health/ready`, the gateway's
admin health aggregation, service messaging counters, RabbitMQ management,
Hangfire, and Jaeger.
