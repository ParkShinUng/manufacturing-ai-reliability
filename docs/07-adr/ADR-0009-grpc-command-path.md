# ADR-0009 — gRPC for Safety Supervisor → Control Service command path
Status: Accepted

## Context
Kafka is ideal for replayable data/events, but making command execution synchronously dependent on Kafka would couple safe operation to the analytics/event backbone.

## Decision
Safety Supervisor sends bounded command intents to Control Service using versioned gRPC/protobuf. Control Service publishes command outcomes to Kafka for audit/observation.

## Why
- explicit request/result semantics for commands;
- strongly versioned language-neutral contract;
- low overhead and clear timeout/cancellation handling;
- command path remains independent of Kafka availability;
- preserves a single equipment-write owner.

## Consequences
Control Service must implement idempotency because gRPC retry can redeliver requests. Kafka remains the audit/event path, not the command authority.
