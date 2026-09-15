# ADR-0006 — JSON contracts for baseline
Status: Accepted

## Decision
Use versioned JSON event payloads initially.

## Why
Portfolio reviewers can inspect them easily, cross-language integration is simple, and throughput requirements have not yet proven a need for binary serialization.

## Revisit trigger
Measured serialization/network cost becomes a bottleneck. Any switch to Avro/Protobuf requires an ADR including schema-registry and migration strategy.
