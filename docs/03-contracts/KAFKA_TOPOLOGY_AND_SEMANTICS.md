# Kafka Topology and Delivery Semantics

> Closes: GAP-007 (DLQ), GAP-008 (missing topics), GAP-011 (naming), GAP-050 (offsets/replay),
> GAP-051 (`sequence`), GAP-052 (buffering), GAP-053 (retry/timeout profiles), GAP-054 (ordering
> preconditions), GAP-055 (partitions/retention), GAP-056 (consumer groups), GAP-057 (lag alerting).
> Status: v0.3 normative.

## 1. Topic naming

v0.2 used plural forms (`factory.telemetry.v1`, `factory.predictions.v1`,
`factory.safety-decisions.v1`). **The v0.2 plural names are retained** — renaming a canonical
contract for cosmetic consistency would be exactly the undocumented drift the process forbids
(GAP-011). New topics follow the same plural convention.

Pattern: `factory.<aggregate-plural>.v<major>`

## 2. Topic register

| Topic | Producer | Consumers | Key | Partitions | RF (local / prod-like) | Retention | Cleanup |
|---|---|---|---|---|---|---|---|
| `factory.telemetry.v1` | edge-gateway | feature-builder, operations-projector, shadow-feature-builder | `equipmentId` | **12** | 1 / 3 | **6 h** | delete |
| `factory.predictions.v1` | prediction-service | safety-supervisor, operations-projector | `equipmentId` | **12** | 1 / 3 | **24 h** | delete |
| `factory.safety-decisions.v1` | safety-supervisor | operations-projector | `equipmentId` | **12** | 1 / 3 | **7 d** | delete |
| `factory.control-outcomes.v1` | control-service | operations-projector | `equipmentId` | **12** | 1 / 3 | **7 d** | delete |
| `factory.equipment-states.v1` | edge-gateway | safety-supervisor, operations-projector | `equipmentId` | **12** | 1 / 3 | **7 d** | compact+delete |
| `factory.model-deployments.v1` | mlops-publisher | safety-supervisor, operations-projector | `modelName` | **1** | 1 / 3 | **∞** | compact |
| `factory.faults.v1` | equipment-simulator | operations-projector | `equipmentId` | **3** | 1 / 3 | **7 d** | delete |

`factory.control-outcomes.v1` and `factory.equipment-states.v1` are **new in v0.3** — ADR-0009 and
`SYSTEM_ARCHITECTURE.md` §7 both required them but neither defined them (GAP-008).

### 2.1 Why 12 partitions

The demo profile is 20 equipment and the load profile is up to 250 (`RUNTIME_BEHAVIOR.md`).
12 partitions divides evenly into common consumer counts (1, 2, 3, 4, 6, 12) and comfortably exceeds
the realistic consumer parallelism for this workload. It is chosen once and **frozen** — see §4.

`factory.model-deployments.v1` uses 1 partition because it is a low-volume compacted log where
global ordering of model-authorization changes matters more than throughput.

### 2.2 Why these retentions

- Telemetry 6 h: long enough for the `FAIL-KAFKA-001` replay demonstration and a feature-window
  rebuild, short enough that 250 equipment × 100 ms does not fill a laptop disk.
- Predictions 24 h: supports shadow-vs-production comparison over a full demo day.
- Decisions / outcomes / states 7 d: audit evidence must outlive a weekend.
- Model deployments infinite + compacted: the authorization log is the safety-relevant history.

Raw 100 ms telemetry is deliberately **not** duplicated into PostgreSQL (`OPEN_DECISIONS` #6); Kafka
is its only store, which is exactly why its retention is a safety-relevant configuration value and
not an afterthought.

## 3. Consumer groups

| Group | Topic(s) | `auto.offset.reset` | Replay-eligible | Notes |
|---|---|---|---|---|
| `cg.safety-supervisor.v1` | predictions, equipment-states, model-deployments | **`latest`** | **NO** | see §5 |
| `cg.operations-projector.v1` | all event topics | `earliest` | **YES** | rebuildable read models |
| `cg.feature-builder.v1` | telemetry | `latest` | YES (offline) | |
| `cg.shadow-feature-builder.v1` | telemetry | `latest` | YES (offline) | ADR-0010 shadow path |
| `cg.<name>.dlq-redrive` | `<topic>.dlq` | `earliest` | manual only | operator-initiated |

Naming: `cg.<service>.v<major>` (GAP-056). A consumer-group rename is a full re-read and is
therefore treated as a breaking change.

## 4. Ordering — the preconditions that make it true

ADR-0001 asserts "partitioning by equipment preserves per-equipment order." That is only true under
conditions v0.2 never stated (GAP-054). All of the following are **mandatory**:

| Setting | Value | Why |
|---|---|---|
| Partition count | **immutable for the life of a `vN` topic** | changing it remaps keys and breaks per-equipment ordering across the change |
| `enable.idempotence` | `true` | without it, a producer retry can reorder within a partition |
| `max.in.flight.requests.per.connection` | `≤ 5` | required bound for idempotent ordering guarantees |
| `acks` | `all` | |
| Partitioner | default murmur2 on the key | must not be overridden |

**A partition-count change requires a new topic major version.** This is stated as a rule because
the alternative — quietly adding partitions under load — silently breaks the one ordering property
the safety design depends on.

Ordering is guaranteed **per `equipmentId` only**. No cross-equipment ordering exists and none may be
assumed.

## 5. Replay policy

Replay is a portfolio deliverable (`FAIL-KAFKA-001`), but replaying predictions into the Safety
Supervisor would re-issue real equipment commands (GAP-027). Resolution, per **DEC-002**:

1. `cg.safety-supervisor.v1` uses `auto.offset.reset=latest` and has **no documented reset
   procedure**. Replay is performed only on projector/analytics groups.
2. The prediction TTL gate compares `predictedAtUtc` against **wall-clock now**, never against the
   consumed record's position. A replayed prediction is therefore already expired by construction.
   *This comparison basis is normative and is the primary defence.*
3. Control Service independently rejects any command whose `sourcePredictionAtUtc` exceeds the TTL
   bound (`COMMAND_SOURCE_STALE`).

Any one of the three is sufficient; all three are required so that no single operator error or bug
re-animates historical predictions.

## 6. Offset commit semantics

| Rule | Value |
|---|---|
| Commit mode | **manual, after successful processing** (`enable.auto.commit=false`) |
| Delivery semantics | **at-least-once** |
| Exactly-once | **not claimed** — MASTER_SPEC principle 6 |
| Consumer restart (general) | resumes from last committed offset; duplicates are expected and must be absorbed by idempotency |
| Consumer restart (**safety-supervisor, predictions topic**) | **OVERRIDE: explicit `seekToEnd` on `factory.predictions.v1` at startup.** Committed offsets are deliberately ignored for this one consumer/topic pair. See §6.1. |
| `max.poll.interval.ms` | 300 000 |
| `session.timeout.ms` | 45 000 |
| `heartbeat.interval.ms` | 3 000 |
| `min.insync.replicas` | **1 local / 2 production-like** — required companion to `acks=all`; without it `acks=all` silently degrades to a single replica when the ISR shrinks |

### 6.1 Safety Supervisor startup override (DEC-002)

The Supervisor performs an explicit **`seekToEnd`** on `factory.predictions.v1` at every startup,
discarding any backlog accumulated while it was down.

`auto.offset.reset=latest` alone is **not** sufficient: it applies only to a group with no committed
offset, so a *restarting* Supervisor with committed offsets would otherwise grind through a backlog
of predictions, rejecting each one on TTL. The seek is therefore an explicit startup step, not a
consequence of configuration.

> **Intended behaviour, stated so it is not mistaken for a defect:** after any Supervisor downtime
> exceeding the prediction TTL, every prediction produced during that downtime is intentionally
> discarded. The equipment operates on `SAFE_FALLBACK` until the next fresh prediction arrives, at
> most one inference cadence (5 s) later. A prediction older than its TTL has no value, so replaying
> it would only delay the first useful decision.

**Interaction with the control lease (DEC-001).** On restart the Supervisor holds no valid lease. It
must call `AcquireControlLease` before it can command, and that call increments `controlEpoch`,
fencing any command it issued before the restart. Mode is already `SAFE_FALLBACK` because
`AI_ASSISTED` is never restored across a restart (`CONTROL_MODE_STATE_MACHINE.md` §7). The two
mechanisms therefore agree rather than conflict: a restarted Supervisor starts fenced, unauthorised,
and on fallback, and must re-earn AI authority through transition M5.

`factory.equipment-states.v1` and `factory.model-deployments.v1` are **not** seek-to-end. Both are
compacted state topics that the Supervisor must read in full to learn current equipment state and
current model authorisation.

Auto-commit is forbidden because it commits offsets for records that may not have been processed,
which converts at-least-once into silent at-most-once on a crash.

## 7. Duplicate identity

A duplicate is defined **per topic**, so consumers do not have to guess:

| Topic | Duplicate identity |
|---|---|
| `factory.telemetry.v1` | `(equipmentId, sequence)` |
| `factory.predictions.v1` | `predictionId` |
| `factory.safety-decisions.v1` | `decisionId` |
| `factory.control-outcomes.v1` | `commandId` |
| `factory.equipment-states.v1` | `(equipmentId, stateSequence)` |
| `factory.model-deployments.v1` | `(partitionKey, modelVersion, authorizationSequence)`; WATERMARK records use the sentinel key `__watermark__` so liveness records compact to one retained row and never displace a model's authorization |
| `factory.faults.v1` | `faultInjectionId` |

## 8. `sequence` semantics (GAP-051)

| Property | Rule |
|---|---|
| Scope | **per `equipmentId`**, assigned by the equipment/simulator, not the gateway |
| Monotonicity | strictly increasing by 1 per telemetry sample |
| Reset | resets to 0 **only** on equipment restart, which must coincide with a `sourceEpochMs` reset |
| Restart detection | `sequence` decrease **without** a `sourceEpochMs` reset is a fault, flagged `SEQUENCE_GAP` and escalated |
| Gap | any increment > 1 raises `SEQUENCE_GAP` with the gap size recorded |
| Consumer duty | gaps are **observable telemetry loss**; they must be counted in `telemetry_sequence_gaps_total`, never silently ignored |
| Width | `uint64` — cannot wrap in any realistic run |

Assigning `sequence` at the **equipment**, not the gateway, is what makes gateway-side loss
detectable. A gateway-assigned sequence would be continuous even when the gateway dropped samples.

## 9. Dead-letter queues (GAP-007)

| Rule | Value |
|---|---|
| DLQ topic | `<source-topic>.dlq` (e.g. `factory.predictions.v1.dlq`) |
| Partitions / RF | mirror the source topic |
| Retention | 30 d |
| Max attempts before DLQ | **3** |
| Backoff between attempts | 1 s → 2 s → 4 s, ±20 % jitter |
| Required DLQ headers | `x-dlq-reason`, `x-dlq-source-topic`, `x-dlq-source-partition`, `x-dlq-source-offset`, `x-dlq-attempt-count`, `x-dlq-first-failed-at`, `x-dlq-error-class` |
| Redrive | operator-initiated only, via `cg.<name>.dlq-redrive`; never automatic |
| Alert | any DLQ produce raises an alert immediately |

**Poison-message rule:** a record that fails **schema validation** goes to the DLQ on the *first*
attempt with no retry — retrying a structurally invalid record cannot succeed and only delays the
consumer.

**Safety-critical exception:** the Safety Supervisor does **not** DLQ-and-continue on a prediction it
cannot parse. It rejects the recommendation (`PREDICTION_UNPARSEABLE`), leaves the equipment in its
current safe state, and *then* DLQs the record. Skipping a malformed prediction must never be
mistaken for "no anomaly detected."

## 10. Backpressure and the gateway buffer (GAP-052)

| Parameter | Value |
|---|---|
| Buffer type | bounded in-memory `Channel<T>` (`CODING_STANDARDS.md`) |
| Capacity | **6 000 events** ≈ 30 s at 20 equipment × 100 ms |
| Overflow policy | **drop oldest** |
| On drop | increment `telemetry_dropped_total`, raise `BUFFER_OVERFLOW_DROP` on the next emitted event, log at WARN with the dropped count |
| Blocking | **forbidden** — the buffer must never block the OT poll loop |
| Kafka-down behaviour | continue polling OT, continue buffering, drop oldest on overflow, keep serving local state |

**Drop oldest, never block.** Blocking the poll loop would let a Kafka outage stall OT
communication, which would make the analytics backbone a dependency of the control-adjacent path —
precisely what MASTER_SPEC principle 4 forbids. Dropping the oldest telemetry is a *data* loss,
bounded and counted; blocking would be a *control* risk.

30 s of buffer is deliberately modest: a longer buffer would replay a large burst of stale telemetry
on reconnect, and stale telemetry is rejected by the freshness gate anyway, so buffering beyond the
freshness horizon buys nothing.

## 11. Retry and timeout profiles (GAP-053)

All values are bounded, observable, and jittered (`FAILURE_MODEL.md` retry rule).

| Dependency | Connect timeout | Op timeout | Max attempts | Backoff | Circuit breaker |
|---|---|---|---|---|---|
| OPC UA | 10 s | 1 s | infinite reconnect | 250 ms → 8 s ×2 ±20 % | no (must keep trying) |
| Modbus TCP | 5 s | 250 ms | infinite reconnect | 250 ms → 8 s ×2 ±20 % | no |
| Kafka producer | 10 s | 30 s delivery | 5 | 100 ms → 3.2 s ×2 ±20 % | no |
| Kafka consumer | 10 s | — | infinite | 1 s → 30 s ×2 ±20 % | no |
| PostgreSQL | 5 s | 3 s | 3 | 200 ms → 800 ms ×2 ±20 % | open after 5 consecutive, 30 s |
| MLflow registry | 5 s | 10 s | 3 | 1 s → 4 s ×2 ±20 % | open after 3 consecutive, 60 s |
| gRPC Supervisor→Control | 2 s | **500 ms** | **2** | 100 ms, 200 ms ±20 % | open after 10 consecutive, 15 s |
| Operations API → PostgreSQL | 5 s | 3 s | 2 | 200 ms ×2 | open after 5, 30 s |

**The gRPC command path is deliberately the tightest and the least retried.** Two attempts at 500 ms
means a command either lands within ~1.4 s or is abandoned and re-derived from a fresh decision.
Retrying a stale setpoint for longer is worse than giving up: by the time a long retry succeeds, the
world has moved on, and DEC-003's supersession check would reject it anyway.

Infinite reconnect for OT protocols is correct and is **not** an unbounded retry loop in the
forbidden sense: it is bounded in *rate* (capped backoff) and fully observable
(`reconnect_total`, `protocol_connected`).

## 12. Consumer lag thresholds (GAP-057)

| Topic | Warning | Critical | Rationale |
|---|---|---|---|
| `factory.predictions.v1` (supervisor) | 50 | 200 | at 5 s cadence × 20 equipment, 200 ≈ 50 s behind — beyond prediction TTL, so AI is already being rejected |
| `factory.telemetry.v1` (feature-builder) | 2 000 | 10 000 | 10 000 ≈ 50 s of one equipment's stream |
| `factory.telemetry.v1` (projector) | 5 000 | 50 000 | projections are not control-critical |
| all others | 1 000 | 10 000 | |

**Record age is the primary signal; offset lag is secondary.** A keyed topic with 12 partitions
can concentrate lag on a single partition, so an aggregate offset-lag threshold does not guarantee
per-equipment freshness - lag can look healthy overall while one partition starves. The Supervisor
therefore additionally alerts on **`prediction_record_age_seconds`**, the age of the newest processed
record tracked **per assigned partition**, with warning at **10 s** (one prediction TTL) and critical
at **30 s**.

The supervisor offset thresholds are set where they are because lag beyond prediction TTL is not
merely "slow" - it means the AI path has silently stopped contributing and the system is running on
fallback without anyone having declared an outage.
