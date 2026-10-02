# Service Design — Telemetry / Operational Data (PostgreSQL)

> Closes GAP-080. Owner of every read model and audit record.

## 1. Purpose
Durable operational store for aggregates, predictions, decisions, outcomes, mode history, faults, and
model deployments. Serves the Operations API; **is not in the command path**.

## 2. Responsibilities
Persist the latest reading per equipment per second (OD-015), predictions, safety decisions, control outcomes, mode transitions, fault
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
Migrations are **per owner, per PostgreSQL schema** (OD-016): the `operations` schema is migrated by
operations-service (`src/dotnet/OperationsService/Migrations`), the `control` schema by
control-service in Phase 7. Phase 4 creates `operations` only. The JSON schemas define field
semantics and the tables must not diverge from them.

## 7. Data ownership
| Table | Schema · owner | Rebuildable from Kafka? (OD-014) |
|---|---|---|
| `telemetry_reading_1s` | `operations` · operations-projector | within **6 h** |
| `prediction` | `operations` · operations-projector | within 24 h |
| `safety_decision` | `operations` · operations-projector | within 7 d |
| `control_outcome` | `operations` · operations-projector | within 7 d |
| `equipment_state_history` | `operations` · operations-projector | **no** — the topic is compacted; only each equipment's latest state is replayable |
| `equipment_decommission` | `operations` · operations-projector | **no** — compaction removes a tombstone after `delete.retention.ms`; audit (OD-021) |
| `fault_injection` | `operations` · operations-projector | within 7 d — **not yet created**: `factory.faults.v1` has no contract (OD-020) |
| `model_deployment` | `operations` · operations-projector | latest per model only (compacted) |
| `authorization_watermark` | `operations` · operations-projector | latest only (compacted); one row, the newest `WATERMARK` record |
| **`control_state`** | **`control` · control-service** | **NO — authoritative** |
| **`command_idempotency`** | **`control` · control-service** | **NO — authoritative** |

Past its topic's retention a projection row is the **only copy**. The audit tables —
`safety_decision`, `control_outcome`, `equipment_state_history` — are therefore backed up like
`control_state`, and nothing truncates them (OD-014).

The last two rows are the ones that matter operationally: everything else can be dropped and
rebuilt, but Control Service state is original and must be backed up.

## 8. State model
Time-series tables partitioned by `occurred_at_utc` — `telemetry_reading_1s` by `second_utc` —
**daily** for `telemetry_reading_1s` and `prediction`, monthly for the rest; a partition is dropped
when its upper bound is older than the table's retention (OD-018). Partitions are created on demand
by `operations.ensure_partition()` before a write, in UTC. PostgreSQL requires a partitioned table's
primary key to contain the partition key, so those keys are the duplicate identity **plus**
`occurred_at_utc`; a duplicate delivery carries the same `occurred_at_utc`, so it still lands on the
same row. The schema is `src/dotnet/OperationsService/Migrations/operations/0001_projection_tables.sql`; indexed on
`(equipment_id, occurred_at_utc)` and on `correlation_id` (the index that makes AC-006 a single query
rather than a scan).

## 9. Lifecycle
Each schema's migrations are applied at deploy by its owner (OD-016). A rebuild is a **range
operation** (OD-014): rewind the projector group over an offset range and replace only the rows
whose provenance (`source_topic`, `source_partition`, `source_offset`) lies in that range. **No
rebuild truncates a table**; `control_state` and every row outside the range are untouched.
**Rows are deleted only from `telemetry_reading_1s` and `prediction`** (OD-023): their topics keep
every record within retention, so a replay restores what was deleted. The audit tables are
insert-only, and the tables fed by compacted topics would not get their history back; a replay into
them re-inserts only what is missing.

## 9a. Canonical dump — what "byte-identical" means

AC-028 and AC-046 compare a **canonical export**, not storage (OD-014). Per table: every column in
declaration order; rows ordered by primary key; `timestamptz` as UTC ISO-8601 with millisecond
precision and `Z`; numerics as their shortest round-trip decimal text; booleans `true`/`false`;
`NULL` as `\N`; tab-separated, one row per line, UTF-8. Two projections are identical when their
dumps are equal byte for byte.

That only holds if a projection table has **no non-deterministic column**: its primary key comes
from the topic's duplicate identity (`KAFKA_TOPOLOGY_AND_SEMANTICS.md` §7), every time from the
event, and there is no sequence, no `now()` default and no insertion timestamp. Provenance columns
are deterministic — a replay reads the same offsets.

## 10. Normal flow
Projector consumes → upserts idempotently by the topic's duplicate identity → commits offset.
Idempotent upsert is what makes at-least-once delivery harmless here.

**Tombstones on `factory.equipment-states.v1`** (OD-021) go to `equipment_decommission` — one row
**per tombstone record**, not per decommissioning episode: a redelivery is the same row, a second
tombstone at another offset is a second row. History is never deleted.

| Column | Type | Source |
|---|---|---|
| `equipment_id` | `text NOT NULL` | the record key |
| `decommissioned_at_utc` | `timestamptz NOT NULL` | the record's Kafka `CreateTime`, set by the gateway when it decided the equipment left its inventory (`KAFKA_TOPOLOGY_AND_SEMANTICS.md` §6); stored in the log, so a replay reads it back unchanged |
| `source_topic`, `source_partition`, `source_offset` | `text`, `integer`, `bigint`, all `NOT NULL` | provenance — and the primary key, since a tombstone's identity is the record (§7 of the topology) |

Indexed on `(equipment_id, source_offset)` for the current view. Not partitioned, and **never
deleted**: it is an input to the current view as well as audit, and a deleted decommission would
let an older state record that outlived it — monthly partitions drop up to a month late — make the
equipment current again. One row per decommissioning is bounded by the inventory. Created by
migration `0002`. An equipment is **current** when its newest state
record is at a later offset than its newest tombstone; both share its key and so its partition, which
makes the offsets comparable. An equipment that reappears is current again.

`telemetry_reading_1s` (OD-015): key `(equipment_id, second_utc)`, `second_utc` = `eventTimeUtc`
truncated to the second. The stored reading is the one with the greatest `(eventTimeUtc, sequence)`,
and an upsert replaces it only with a greater one, so the result depends on which records exist and
not on their order: a replay is deterministic, a late record is simply applied, and nothing is held
in memory across a restart. No statistics are computed here; 1 s aggregate features are Phase 5's.

## 11. Failure behaviour
| Failure | Behaviour |
|---|---|
| Unavailable | projections pause on the record that failed and resume in order (OD-022) — nothing dead-lettered, nothing committed past it; Operations API returns 503, at once while its circuit breaker is open; **control watchdog unaffected** |
| Disk pressure | not handled in Phase 4: nothing measures it, and dropping data on a guessed threshold is worse than the retention job's fixed rule. Phase 8 owns a disk metric and its alert (OD-022) |
| Migration failure | deploy aborts; service does not start against a half-migrated schema |

## 12. Timeout / retry / idempotency / ordering
Query timeout 3 s. **No read retries**: one attempt, `503` on an unavailable store. Projection
writes wait out an outage on the record that failed (OD-022). The API's circuit breaker, per
instance: 5 consecutive *unavailable* results open it for 30 s, then one probe request; what counts
as unavailable is listed in OD-022 — a SQL error is a defect, not an outage.
All projection writes idempotent. Ordering per equipment preserved by partition consumption.

## 13. Backpressure
Projector batches up to 500 records or 1 s; bounded in-flight.

## 14. Restart recovery
Projections resume from offsets; a full rebuild is an operator action with a documented runbook.

## 15. Configuration
Retention (OD-023), by whole partition: per-second readings 30 d, predictions 30 d, safety decisions 90 d, control outcomes **1 y** (mode transitions among them), equipment state history 1 y, decommissions indefinite (an input to the current view). Run at start and hourly by `operations.drop_expired_partitions()` as `mair_ops_retention`. Earlier text, kept for the record: decisions/outcomes 90 d, mode history 1 y,
`control_state` indefinite.

## 16. Security
Least-privilege roles: projector write-only to projection tables; API read-only; control-service
read/write only to its two tables. Roles follow the schema boundary (OD-016). No shared superuser
at runtime.

**Implemented for `operations` (OD-023, migration `0003`)**: group roles without login —
`mair_ops_projector` INSERT only on `safety_decision` and `control_outcome`, INSERT/UPDATE/DELETE on
the other projection tables; `mair_ops_reader` SELECT; `mair_ops_retention` EXECUTE on
`drop_expired_partitions` and nothing else. The runtime login is granted these roles and each data
source sets its own on connect, so the API's connection cannot write and the projector's cannot
change an audit row. Migrations run on a separate connection string.

## 17. Observability
`projection_lag_seconds`, `db_query_latency_seconds`, `db_errors_total`, `table_rows`,
`retention_deleted_rows_total`.

## 18. Performance targets (TARGET — unmeasured)
per-second reading write ≥ 250 rows/s; correlation trace query P95 ≤ 200 ms; equipment summary P95 ≤ 100 ms.

## 19. Test strategy
Migrations, **forward-only** (ADR-0024): up from empty; a second run applies nothing; a failing
migration leaves the schema unchanged; an edited applied file is refused; two runners started
together apply each file once. Idempotent upsert under duplicate delivery; rebuild-from-Kafka equivalence
(equal canonical dumps, §9a); retention correctness; AC-047 trace query.

## 20. Acceptance criteria
AC-047, AC-028, AC-029, AC-046; AC-006 in Phase 7 (OD-011).
