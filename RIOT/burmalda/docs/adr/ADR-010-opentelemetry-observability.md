# ADR-010: OpenTelemetry observability

Status: Accepted — 2026-09-17

## Context

One loan request crosses gateway, services, databases, RabbitMQ, and background
workers; isolated logs cannot explain failures.

## Decision

Instrument HTTP, clients, EF Core, runtime metrics, and messaging with
OpenTelemetry. Carry W3C trace context, correlation, and causation in envelopes;
export locally through the Collector to Jaeger.

## Consequences

Operators can follow distributed work and measure latency/errors. Telemetry has
runtime/storage cost and must avoid secrets or sensitive payloads.
