# ADR-0001 — Kafka as durable event backbone
Status: Accepted

## Context
Telemetry has multiple independent consumers (AI, monitoring, persistence) and portfolio demonstrations require replay, lag observability, and consumer isolation.

## Decision
Use Kafka for normalized telemetry and downstream event distribution.

## Why
- durable replay suits failure/recovery demonstrations;
- partitioning by equipment preserves per-equipment order;
- consumer groups isolate workloads;
- lag is a useful operational signal;
- aligns with common enterprise manufacturing-data patterns.

## Not chosen
- direct point-to-point APIs: tight coupling and weak replay story;
- replacing all local control communication with Kafka: would make safe control too dependent on the data backbone.

## Consequences
Must design idempotent consumers, schema evolution, retry/DLQ rules, and avoid claiming exactly-once effects merely because Kafka supports transactional features.
