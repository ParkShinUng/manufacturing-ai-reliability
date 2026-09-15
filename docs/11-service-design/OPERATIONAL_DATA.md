# Service Design — Telemetry / Operational Data (PostgreSQL)

> Closes GAP-080. Owner of every read model and audit record.

## 1. Purpose
Durable operational store for aggregates, predictions, decisions, outcomes, mode history, faults, and
model deployments. Serves the Operations API; **is not in the command path**.

## 2. Responsibilities
Persist 1 s aggregates, predictions, safety decisions, control outcomes, mode transitions, fault
injections, model deployments; back Control Service durable state; support correlation-ID trace
retrieval (AC-006).

## 3. Non-responsibilities
Stores **no raw 100 ms telemetry** (`OPEN_DECISIONS` #6 — Kafka is its only store). Not a source of
truth for anything Kafka already owns, except Control Service state.

## 4. Dependencies / classification
**Control-critical for Control Service state** (epoch, mode, idempotency); control-independent for
everything else. This split is why a database outage rejects new commands but does not stop the
watchdog (F12, `CONTROL_SERVICE.md` §12).

## 5. Inputs / outputs
In: projections from Kafka; direct writes from Control Service. Out: Operations API queries.

## 6. Contracts
Schema migrations in `src/dotnet/OperationsService/Migrations`; the JSON schemas define field
semantics and the tables must not diverge from them.

## 7. Data ownership
| Table | Owner | Rebuildable from Kafka? |
|---|---|---|
| `telemetry_aggregate_1s` | operations-projector | **yes** |
| `prediction` | operations-projector | yes |
| `safety_decision` | operations-projector | yes |
| `control_outcome` | operations-projector | yes |
| `equipment_state_history` | operations-projector | yes |
| `fault_injection` | operations-projector | yes |
| `model_deployment` | operations-projector | yes |
| **`control_state`** | **control-service** | **NO — authoritative** |
| **`command_idempotency`** | **control-service** | **NO — authoritative** |

The last two rows are the ones that matter operationally: everything else can be dropped and
rebuilt, but Control Service state is original and must be backed up.

## 8. State model
Time-series tables partitioned monthly by `occurred_at_utc`; indexed on
`(equipment_id, occurred_at_utc)` and on `correlation_id` (the index that makes AC-006 a single query
rather than a scan).

## 9. Lifecycle
Migrations applied at deploy by a single owner service; projector rebuild = truncate projection
tables and replay from Kafka. `control_state` is **never** truncated by a rebuild.

## 10. Normal flow
Projector consumes → upserts idempotently by the topic's duplicate identity → commits offset.
Idempotent upsert is what makes at-least-once delivery harmless here.

## 11. Failure behaviour
| Failure | Behaviour |
|---|---|
| Unavailable | projections pause and resume from committed offsets; Operations API returns 503; **control watchdog unaffected** |
| Disk pressure | retention job drops the oldest aggregate partitions first |
| Migration failure | deploy aborts; service does not start against a half-migrated schema |

## 12. Timeout / retry / idempotency / ordering
Query timeout 3 s; 2–3 retries; circuit breaker opens after 5 consecutive failures for 30 s.
All projection writes idempotent. Ordering per equipment preserved by partition consumption.

## 13. Backpressure
Projector batches up to 500 records or 1 s; bounded in-flight.

## 14. Restart recovery
Projections resume from offsets; a full rebuild is an operator action with a documented runbook.

## 15. Configuration
Retention: aggregates 30 d, predictions 30 d, decisions/outcomes 90 d, mode history 1 y,
`control_state` indefinite.

## 16. Security
Least-privilege roles: projector write-only to projection tables; API read-only; control-service
read/write only to its two tables. No shared superuser at runtime.

## 17. Observability
`projection_lag_seconds`, `db_query_latency_seconds`, `db_errors_total`, `table_rows`,
`retention_deleted_rows_total`.

## 18. Performance targets (TARGET — unmeasured)
1 s aggregate write ≥ 250 rows/s; correlation trace query P95 ≤ 200 ms; equipment summary P95 ≤ 100 ms.

## 19. Test strategy
Migration up/down; idempotent upsert under duplicate delivery; rebuild-from-Kafka equivalence
(a rebuilt projection must be byte-equivalent); retention correctness; AC-006 trace query.

## 20. Acceptance criteria
AC-006, AC-028, AC-029.
