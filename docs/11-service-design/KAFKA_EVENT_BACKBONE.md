# Service Design — Kafka Event Backbone (infrastructure)

> Not an application service, but a designed component with owners, failure behaviour, and
> acceptance criteria. Full topology: `docs/03-contracts/KAFKA_TOPOLOGY_AND_SEMANTICS.md`.

## 1. Purpose
Durable, replayable, consumer-isolated event distribution for telemetry, predictions, decisions,
outcomes, states, and model authorizations (ADR-0001).

## 2. Responsibilities
Durable ordered storage per partition; consumer-group isolation; replay for projection groups;
compaction for state topics; lag visibility; DLQ topics.

### 2.1 The shared consume-validate-DLQ component (OD-009)

Phase 3 also delivers the **one** consumer path every later consumer is built on — the projector,
the feature builder, the Supervisor. It exists because `AC-027` is consumer behaviour and the rules
it must follow are written once in `KAFKA_TOPOLOGY_AND_SEMANTICS.md` §9; implementing them per
consumer is how two consumers end up disagreeing about what "invalid" means.

Its scope is exactly those rules and nothing more:

- validate each record against its **authoritative JSON Schema** (`ADR-0017`);
- a schema-invalid record goes to `<topic>.dlq` on the **first** attempt, with no retry;
- every required DLQ header is set;
- the source offset is committed **only after** the DLQ produce succeeds;
- **no** automatic redrive — that is operator-initiated (§9);
- a **tombstone** goes to the consumer's tombstone handler only on a topic whose register entry
  allows one, and is an invalid record everywhere else (OD-021, added 2026-10-01 — a rule from the
  contract, `OD-008`, not from a consumer's convenience).

It also reports when it was last **caught up** (OD-018): checked by its own loop after a poll that
returned nothing, against the high watermark of every assigned partition, using its position — or,
before it has consumed anything there, the group's committed offset, then the reset policy. The
Operations API's readiness gate and staleness header read that time; nothing else does.

Adopting it is a condition of the phases that add consumers, not a recommendation. It is **not** a
consumer framework: no routing, no handler registry, no configuration surface beyond the topic, the
schema and the handler.

## 3. Non-responsibilities
**Not a command transport** (ADR-0009). Not a database. Not a source of truth for equipment state
beyond its compacted projection. The shared consumer component in §2.1 is **not** a place for
business logic: it validates, routes invalid records to the DLQ, and commits — a handler's own
failures are the handler's, and the Supervisor's reject-then-DLQ exception (§9) stays in Phase 6. Provides no exactly-once effect guarantee (MASTER_SPEC principle 6).

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

**Phase 3 counts; Phase 8 exports** — the precedent `EDGE_GATEWAY.md` §17 set in Phase 2. Codex's
Phase 3 verification found these missing entirely (`P3-COD-002`), although `IMPLEMENTATION_PLAN.md`
lists them as a Phase 3 deliverable:

| Metric | Phase 3 measurement |
|---|---|
| `kafka_consumer_lag{group,topic,partition}` | `ContractConsumer.Lag` — records between the newest settled one and the high watermark, per partition |
| `prediction_record_age_seconds{partition}` | `ContractConsumer.RecordAge()` — age of the newest **settled** record per partition, from the record's own timestamp; the primary signal in §12 |
| `dlq_messages_total{topic,reason}` | `ContractConsumer.DeadLetteredByReason` |
| `producer_errors_total` | `KafkaTelemetrySink.DeliveryFailures`, `KafkaEquipmentStateSink.DeliveryFailures` |
| `under_replicated_partitions` | a **broker** metric, not a client measurement — Phase 8 reads it from the broker |

Record age is measured from the record, not from when it was read: a record stamped a minute ago
and consumed now is a minute old, which is what makes it the freshness signal §12 wants.

## 18. Performance targets (TARGET — unmeasured)
2 500 telemetry ev/s at load profile; P95 produce ≤ 20 ms; broker restart recovery ≤ 30 s.

## 19. Test strategy
Contract tests per topic; replay test (**AC-045**) proving a **replay-eligible** group replays while the
Supervisor does not; broker restart (`FAIL-KAFKA-001`); DLQ redrive; partition-count immutability
assertion in the bootstrap job.

## 20. Acceptance criteria
**AC-045**, AC-026, AC-027. `AC-003` (replay moves no equipment) is Phase 6 and `AC-046` (read
models rebuild byte-identically) is Phase 4 — see OD-007.
