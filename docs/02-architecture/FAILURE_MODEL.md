# Failure Model

> Closes: GAP-020 (OOD contradiction removed), GAP-022 (Supervisor unavailable), GAP-091 (matrix
> shape), and adds the CONTROL-CRITICAL classification.
> Status: v0.3 normative. Replaces the v0.2 10-row / 4-column table.

## 1. Classification

Every failure is classified by whether it can affect the **equipment control path**:

- **CC — CONTROL-CRITICAL**: can affect the ability to hold or change an equipment setpoint safely.
- **CI — CONTROL-INDEPENDENT**: degrades data, AI, or observability only. **Must never** affect
  control. If a CI failure is ever found to affect control, that is an architecture defect, not an
  operational event.

The value of this column is that it makes MASTER_SPEC principle 4 falsifiable: the claim "analytics
failure cannot block local control" is testable exactly because every row is labelled.

## 2. Three-layer authority (DEC-001)

Failure behaviour only makes sense against the authority model:

| Layer | Owner | Authority | Survives |
|---|---|---|---|
| **L1** AI-advisory | Safety Supervisor | propose bounded setpoint | — |
| **L2** Application control | **Control Service** — sole *application* equipment writer | apply, clamp, fence, watchdog-fallback | Supervisor death |
| **L3** Equipment self-protection | Simulated equipment | protective trip, dead-man revert | **total platform death** |

FR-035 and ADR-0002 constrain *application components*; L3 is the equipment protecting itself, in the
same class as an over-temperature trip, and is not an application writer.

## 3. Failure matrix

Abbreviations: **Det.** = detection, **Ctl** = control impact, **Data** = data impact,
**Op vis.** = operator visibility.

| # | Failure | Class | Det. | Immediate behaviour | Ctl impact | Data impact | Fallback | Recovery | Op vis. | Alert | Acceptable consequence | **Unacceptable** |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| F01 | Equipment protocol disconnect | CC | session timeout 3 s | state → `OFFLINE`; telemetry stops | setpoint frozen at last applied; L3 dead-man reverts at 30 s | gap in stream | L3 dead-man | auto reconnect, bounded backoff | dashboard state | WARN | bounded telemetry gap | equipment continues at AI-elevated rate indefinitely |
| F02 | OPC UA / Modbus read timeout | CC | 250 ms / 1 s op timeout | retry with backoff; quality degrades | none if transient | `STALE_READING` | — | auto | metric | INFO | brief staleness | stale value presented as fresh |
| F03 | Edge Gateway crash | CC | health probe, telemetry stops | telemetry halts; Supervisor sees stale telemetry | AI rejected on freshness → `SAFE_FALLBACK` | in-flight buffer lost (≤ 6 000 events) | Supervisor fallback | restart, reconnect | dashboard | CRIT | bounded loss ≤ 30 s | silent loss with no gap indication |
| F04 | Edge Gateway restart | CC | startup event | sequence tracking resets with `sourceEpochMs` | none | `SEQUENCE_GAP` raised | — | auto | metric | flagged gap | undetected gap |
| F05 | Gateway buffer overflow | CI | `telemetry_dropped_total` | drop **oldest**, never block the poll loop | **none** | bounded loss, `BUFFER_OVERFLOW_DROP` | — | drains when Kafka returns | metric | WARN | bounded data loss | blocking the OT poll loop |
| F06 | Kafka unavailable | **CI** | producer errors | gateway buffers; Supervisor receives no predictions | **none** — command path is gRPC (ADR-0009) | ingestion paused | AI rejected on freshness → fallback | auto reconnect + replay | dashboard | CRIT | AI unavailable | control blocked by Kafka |
| F07 | Kafka broker restart | CI | reconnect | producers retry, consumers rebalance | none | brief pause | — | auto | metric | brief pause | undocumented loss claim |
| F08 | Consumer lag growth | CI | lag + `prediction_record_age_seconds` | predictions age out | none | staleness | AI rejected on TTL | scale/repair consumer | dashboard | WARN→CRIT | AI degrades to fallback | stale prediction accepted as fresh |
| F09 | Duplicate event | CI | per-topic duplicate identity | deduplicated by consumer | none | none | — | — | metric | INFO | none | duplicate command effect |
| F10 | Out-of-order prediction | CC | gate 4 `PREDICTION_ORDERED` | older prediction discarded | none | none | — | — | metric | INFO | discard | older intent overwriting newer |
| F11 | **Kafka replay into Supervisor** | CC | n/a — prevented by design | `seekToEnd` at startup; wall-clock TTL; Control Service `COMMAND_SOURCE_STALE` | none | none | — | — | audit | — | replay is inert | **historical predictions moving live equipment** |
| F12 | PostgreSQL unavailable | CI | connection errors, breaker | projections pause; Operations API degraded | **none** | read models stale | — | auto reconnect, replay from Kafka | dashboard | CRIT | dashboard stale | control blocked by database |
| F13 | Operations API unavailable | CI | health probe | dashboard cannot read | **none** | none | — | restart | dashboard down | WARN | UI outage | any control impact |
| F14 | Dashboard unavailable | CI | frontend health | none | **none** | none | — | restart | — | INFO | UI outage | any control impact |
| F15 | AI inference crash | CI | health + missing predictions | no predictions produced | AI rejected → `SAFE_FALLBACK` | prediction gap | fallback rate | restart | dashboard | WARN | AI unavailable | control loss (**AC-004**) |
| F16 | AI inference timeout | CI | > 500 ms budget | prediction skipped for that cycle | none this cycle | gap | fallback if sustained | auto | metric | INFO | one cycle skipped | unbounded wait |
| F17 | Stale prediction | CC | gate 3 TTL vs wall clock | rejected `PREDICTION_STALE` | fallback | none | fallback rate | next fresh prediction | dashboard reason | INFO | AI rejected | stale recommendation applied |
| F18 | OOD input | CC | gate 8 | **hard reject** `OOD_HIGH` | fallback | none | fallback rate | when input returns in-distribution | dashboard reason | WARN | AI rejected | OOD recommendation applied |
| F19 | Low confidence | CC | gate 9 | rejected `CONFIDENCE_LOW` | fallback | none | fallback rate | auto | dashboard reason | INFO | AI rejected | low-confidence value applied |
| F20 | Bad sensor quality | CC | gate 6 | rejected `SENSOR_QUALITY_BAD` | fallback | `null` + flag | fallback rate | sensor recovery | dashboard reason | WARN | AI rejected | fabricated value fed to gates |
| F21 | Incomplete feature window | CC | gate 7 | **no prediction produced at all** | fallback | none | fallback rate | window refills | metric | INFO | AI unavailable | prediction from a half-empty window |
| F22 | Invalid / unparseable model | CI | load failure at startup | model not loaded; service unhealthy | AI unavailable → fallback | none | fallback rate | redeploy | dashboard | CRIT | AI unavailable | invalid model serving predictions |
| F23 | **Safety Supervisor unavailable** | **CC** | **Control Service watchdog, 12 s silence** | **Control Service autonomously writes fallback rate, increments `controlEpoch`** | **mode → `SAFE_FALLBACK`; equipment driven to 60 %** | decisions stop | **L2 watchdog, then L3 dead-man at 30 s** | Supervisor restarts, `AcquireControlLease`, re-earns AI via M5 after 30 s | dashboard mode | **CRIT** | AI unavailable, equipment safe | **equipment holding an AI-elevated rate with nobody supervising** |
| F24 | Late command after watchdog fallback | **CC** | `controlEpoch` mismatch | rejected `COMMAND_EPOCH_STALE` | none | none | — | Supervisor re-acquires lease | audit | WARN | command fenced | **a delayed AI command undoing a fallback** |
| F25 | Control Service unavailable | **CC** | health probe; Supervisor gRPC errors | no commands can be applied | setpoint frozen; **L3 dead-man reverts at 30 s** | outcomes stop | **L3 only** | restart; mode reloaded, `AI_ASSISTED` never restored | dashboard | **CRIT** | equipment reverts to safe default | equipment held at elevated rate past the dead-man |
| F26 | Equipment protocol write failure | CC | write exception | 2 attempts, then `COMMAND_PROTOCOL_FAILED` | command not applied; previous setpoint stands | none | previous setpoint | next decision cycle | dashboard reason | WARN | command dropped | partial or ambiguous write |
| F27 | MLflow unavailable | **CI** | registry errors | **no effect on the Supervisor** — authorization is consumed from Kafka | **none** | training/registry impaired | — | auto | dashboard | WARN | lifecycle ops impaired | **a quarantined model retaining authority** |
| F28 | `mlops-publisher` down | **CC** | **missing `AuthorizationWatermark` > 90 s** | all models treated unauthorized → fallback | fallback | authorization frozen | fallback rate | publisher restart | dashboard | **CRIT** | AI unavailable | **silent failure to propagate a quarantine** |
| F29 | Kubernetes pod restart | CC/CI | probe | per-service behaviour above | per-service | per-service | per-service | auto | dashboard | INFO | bounded restart | unbounded crash loop |
| F30 | Network partition (Supervisor ↔ Control) | **CC** | gRPC errors + watchdog | watchdog fires; epoch increments; Supervisor fenced on return | fallback | none | L2 watchdog | Supervisor re-acquires lease | dashboard | CRIT | fallback during partition | **split-brain: two authorities writing different setpoints** |
| F31 | Clock skew beyond budget | **CC** | `clock_skew_seconds` > 250 ms | freshness gates may misjudge; skew added on the permissive side | possible spurious fallback | timestamps suspect | fallback | NTP resync | dashboard | WARN | conservative fallback | **stale data accepted as fresh** |
| F32 | Observability stack unavailable | CI | scrape failure | metrics/traces lost | **none** | telemetry unaffected | — | restart | — | WARN | blind operation | control blocked by observability |

## 4. Retry rule

Retries must be **bounded, observable, and jittered**. Numeric profiles per dependency are in
`docs/03-contracts/KAFKA_TOPOLOGY_AND_SEMANTICS.md` §11. Infinite retry loops without state and
metrics are forbidden. OT protocol reconnect is infinite in *count* but bounded in *rate* (capped
backoff) and fully observable, which satisfies the rule.

## 5. Degraded modes

Full ownership, transitions, guards, precedence, and restart behaviour are in
`docs/02-architecture/CONTROL_MODE_STATE_MACHINE.md`. Summary:

- `NORMAL_RULE` — deterministic operation; AI not in use and nothing is wrong.
- `AI_ASSISTED` — a valid AI recommendation accepted within bounded authority.
- `SAFE_FALLBACK` — AI should be in use but is not trustworthy; deterministic conservative target.
- `STOP_REQUIRED` — rule condition requiring stop; **simulator-only demonstration**, not a certified
  safety action (MASTER_SPEC §2).

> **Removed in v0.3:** the v0.2 statement that OOD may "reject **or reduce AI authority** per model
> policy." It contradicted MASTER_SPEC's "any failed gate rejects." OOD is a **hard reject**
> (DEC-005). Graded authority is deliberately deferred.

## 6. What the matrix asserts, and how it is proven

| Assertion | Proven by |
|---|---|
| No CI failure affects control | F05, F06, F12, F13, F14, F15, F27, F32 rows + `FAIL-KAFKA-001`, `FAIL-AI-001` |
| Supervisor death does not strand equipment | F23 + watchdog test |
| A late command cannot undo a fallback | F24 + epoch fencing test |
| Replay cannot move equipment | F11 + `FAIL-KAFKA-001` |
| A quarantine cannot be silently lost | F28 + watermark timeout test |
