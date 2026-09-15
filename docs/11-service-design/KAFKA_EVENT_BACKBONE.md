# Service Design — Kafka Event Backbone (infrastructure)

> Not an application service, but a designed component with owners, failure behaviour, and
> acceptance criteria. Full topology: `docs/03-contracts/KAFKA_TOPOLOGY_AND_SEMANTICS.md`.

## 1. Purpose
Durable, replayable, consumer-isolated event distribution for telemetry, predictions, decisions,
outcomes, states, and model authorizations (ADR-0001).

## 2. Responsibilities
Durable ordered storage per partition; consumer-group isolation; replay for projection groups;
compaction for state topics; lag visibility; DLQ topics.

## 3. Non-responsibilities
**Not a command transport** (ADR-0009). Not a database. Not a source of truth for equipment state
beyond its compacted projection. Provides no exactly-once effect guarantee (MASTER_SPEC principle 6).

## 4. Dependencies / classification
Kafka is **CONTROL-INDEPENDENT** (F06). Its total loss must not prevent a setpoint being held or
driven to fallback; the command path is gRPC and the L2 watchdog plus L3 dead-man do not consult it.

## 5. Inputs / outputs
7 registered topics plus `.dlq` companions — see the topic register.

## 6. Contracts
All `contracts/jsonschema/v1/*`; envelope per DEC-007.

## 7. Data ownership
Owns event durability and ordering **per `equipmentId` partition only**. No cross-equipment ordering
exists and none may be assumed.

## 8. State model
Per topic: partitions (12, **immutable for the life of a vN topic**), RF (1 local / 3 prod-like),
`min.insync.replicas` (1 / 2), retention, cleanup policy.

## 9. Lifecycle
KRaft single broker locally (`OPEN_DECISIONS` #7); topics created declaratively by an idempotent
bootstrap job; **no auto-topic-creation** — an accidentally auto-created topic would have the wrong
partition count and silently break per-equipment ordering.

## 10. Normal flow
Producers: `enable.idempotence=true`, `acks=all`, in-flight ≤ 5, key = `equipmentId`.
Consumers: manual offset commit after processing, at-least-once.

## 11. Failure behaviour
| Failure | Behaviour |
|---|---|
| Broker down | producers buffer; consumers reconnect; **control unaffected** |
| Broker restart | rebalance; brief pause; measured, not claimed, in `FAIL-KAFKA-001` |
| Under-replicated | alert; local single-broker profile cannot satisfy `min.insync=2` and is therefore not a HA claim |
| Poison message | DLQ on **first** attempt for schema-invalid records — retrying a structurally invalid record cannot succeed |
| Consumer lag | record-age alerting primary, offset lag secondary |

## 12. Timeout / retry / idempotency / ordering
See `KAFKA_TOPOLOGY_AND_SEMANTICS.md` §11; duplicate identity per topic in §7; ordering preconditions
in §4.

## 13. Backpressure
Producer-side bounded buffers in each service; broker-side retention bounds storage.

## 14. Restart recovery
Consumers resume from committed offsets, **except** the Safety Supervisor, which `seekToEnd`s on
predictions (§6.1).

## 15. Configuration
Declarative topic manifest checked into `deploy/`; version-controlled and diffable.

## 16. Security
Local: private compose network. Production-like: TLS + SASL, per-service principals, per-topic ACLs.
No service holds write ACLs to a topic it does not produce.

## 17. Observability
`kafka_consumer_lag{group,topic,partition}`, `prediction_record_age_seconds{partition}`,
`producer_errors_total`, `dlq_messages_total{topic,reason}`, `under_replicated_partitions`.

## 18. Performance targets (TARGET — unmeasured)
2 500 telemetry ev/s at load profile; P95 produce ≤ 20 ms; broker restart recovery ≤ 30 s.

## 19. Test strategy
Contract tests per topic; replay test (AC-003) proving **projector** groups replay while the
Supervisor does not; broker restart (`FAIL-KAFKA-001`); DLQ redrive; partition-count immutability
assertion in the bootstrap job.

## 20. Acceptance criteria
AC-003, AC-026, AC-027.
