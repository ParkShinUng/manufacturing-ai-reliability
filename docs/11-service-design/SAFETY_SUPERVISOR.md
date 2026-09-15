# Service Design — `safety-supervisor`

> Runtime: C#/.NET 10, `BackgroundService`.
> Closes: GAP-090 for this service. Implements DEC-002, DEC-005, DEC-009.
> L1 in the three-layer authority model. **Advisory-to-control translator, not a control authority.**

## 1. Purpose

Convert probabilistic AI recommendations into deterministic, bounded, auditable command intents — or
into an explicit, reasoned rejection. It is the only place in the platform where a model output is
allowed to influence a setpoint, and it is deliberately deterministic so that influence is bounded.

## 2. Responsibilities

- evaluate the 13 canonical safety gates in order (`AI_SAFETY_AND_MLOPS.md` §2);
- emit a `SafetyDecision` for **every** evaluation, accepted or rejected (FR-032);
- hold a control lease and heartbeat to Control Service;
- issue bounded command intents when all gates pass;
- maintain model authorization state from Kafka;
- request fallback when gates fail.

## 3. Non-responsibilities

- **owns no equipment credentials** and performs no equipment write;
- **does not own control mode** — Control Service does (this is the v0.3 change; mode must be owned
  by the component that survives Supervisor death);
- does not run inference;
- cannot grant itself authority;
- cannot release `STOP_REQUIRED`.

## 4. Dependencies

| Dependency | Classification | Failure behaviour |
|---|---|---|
| Control Service (gRPC) | Control-critical | cannot command; Control Service watchdog takes over |
| Kafka `factory.predictions.v1` | Non-control-critical | no predictions → reject on freshness → fallback |
| Kafka `factory.equipment-states.v1` | Control-relevant input | stale state → gate 10 fails → fallback |
| Kafka `factory.model-deployments.v1` | Control-relevant input | watermark timeout → all models unauthorized |
| Safety config | Control-critical | invalid → startup abort |
| **MLflow** | **none — deliberately no dependency** | an MLflow outage has zero effect (DEC-009) |

The MLflow row is the point of DEC-009: authorization is consumed from a compacted Kafka topic, so
the safety path never calls the registry.

## 5. Inputs / outputs

**In:** predictions, equipment states, model authorizations (Kafka); telemetry freshness summary.
**Out:** `factory.safety-decisions.v1`; `ApplyCommand`/`Heartbeat`/`AcquireControlLease` (gRPC).

## 6. Canonical contracts

`contracts/jsonschema/v1/prediction.schema.json`, `safety-decision.schema.json`,
`equipment-state.schema.json`, `model-authorization.schema.json`,
`contracts/proto/control/v1/control.proto`.

## 7. Data ownership

**Owns:** gate evaluation logic, thresholds (from config), decision history, per-equipment last
processed `predictedAtUtc`, current lease/epoch.
**Owns no** durable cross-service state. This is deliberate — after the DEC-003 revision the
Supervisor holds no counter whose loss could brick the control path.

## 8. State model (in-memory, rebuildable)

```text
perEquipment:
  lastProcessedPredictedAtUtc   -- gate 4 monotonic ordering
  lastTelemetrySummary          -- freshness + quality
  currentEquipmentState         -- from compacted topic
  outstandingCommandId          -- at most ONE in flight per equipment
modelAuthorizations             -- compacted state
lastAuthorizationWatermarkAtUtc -- publisher liveness
controlEpoch, leaseExpiresAtUtc -- per equipment
```

Everything here is rebuildable from Kafka plus a lease acquisition, which is why a Supervisor restart
is cheap and safe.

## 9. Lifecycle

1. load + validate config — abort on invalid;
2. consume `factory.model-deployments.v1` from **earliest** (compacted) until caught up;
3. consume `factory.equipment-states.v1` from **earliest** (compacted) until caught up;
4. **`seekToEnd` on `factory.predictions.v1`** — discard the downtime backlog (DEC-002, §6.1 of
   `KAFKA_TOPOLOGY_AND_SEMANTICS.md`);
5. `AcquireControlLease` for all equipment — this increments `controlEpoch`, fencing anything issued
   before the restart;
6. begin heartbeating; become ready.

Steps 2–3 read fully; step 4 discards. The asymmetry is intentional: *state* must be complete,
*events* must be fresh.

## 10. Normal decision flow

```text
on prediction P for equipment E:
  1  gates 1..13 in order (AI_SAFETY_AND_MLOPS.md 2)
  2  record EVERY gate result (short-circuit the decision, exhaustive for the record)
  3  all pass  -> clamp to bounds and delta -> issue ApplyCommand
     any fail  -> decision REJECT/FALLBACK with reason codes
  4  publish SafetyDecision (always, both paths)
  5  update lastProcessedPredictedAtUtc
```

Recording every gate rather than only the first failure is what makes a rejection diagnosable rather
than merely asserted — an operator seeing `OOD_HIGH` can also see that confidence and freshness were
fine.

**At most one outstanding command per equipment.** With a 2 s command TTL against a 5 s cadence, this
local invariant is sufficient to prevent concurrent validity — no distributed coordination needed.

## 11. Failure behaviour

| Failure | Behaviour |
|---|---|
| No predictions arriving | gate 3 fails → `PREDICTION_STALE` → fallback |
| Prediction unparseable | **reject** `PREDICTION_UNPARSEABLE`, equipment stays safe, **then** DLQ |
| Older prediction after newer | discard, `PREDICTION_SUPERSEDED` |
| Equipment state stale > 10 s | gate 10 fails → fallback |
| Watermark missing > 90 s | **all models unauthorized** → `MODEL_AUTHORIZATION_STALE` → fallback |
| Control Service unreachable | log, alert, **stop commanding**; its watchdog will act |
| `COMMAND_EPOCH_STALE` returned | re-acquire lease, **re-derive from current telemetry**; never resend |
| Own config invalid | gate 1 fails → `SUPERVISOR_UNHEALTHY` → no command |
| Kafka unavailable | inputs stale → gates fail → fallback |

The unparseable-prediction row matters: the Supervisor must **not** DLQ-and-continue as a normal
consumer would. Skipping a malformed prediction must never be mistaken for "no anomaly detected."

## 12. Timeout / retry / idempotency / ordering

| Aspect | Rule |
|---|---|
| Gate evaluation budget | ≤ 50 ms |
| gRPC command | 500 ms deadline, 2 attempts (`KAFKA_TOPOLOGY_AND_SEMANTICS.md` §11) |
| Heartbeat | every 3 s (4× margin inside the 12 s watchdog) |
| Lease renewal | at 50 % of the 30 s lease |
| Idempotency | `equipmentId:decisionId` |
| Ordering | strictly monotonic `predictedAtUtc` per equipment (gate 4) |
| Offsets | manual commit after the decision is published |

Committing the offset only after the decision is published means a crash mid-decision replays that
prediction — which is safe, because it will either still pass its gates or now fail on freshness.

## 13. Backpressure

Bounded channel of 1 000 predictions, drop **oldest** on overflow. Dropping the newest would be
exactly wrong: the freshest prediction is the only one with decision value.

## 14. Restart recovery

See §9. Invariants: no pre-restart command survives (epoch fence); no stale prediction is processed
(`seekToEnd`); `AI_ASSISTED` is not restored (Control Service downgrades it); AI authority must be
re-earned through M5 after 30 s of clean gates.

## 15. Configuration

Thresholds come from the **model authorization record**, not from local config — `oodThreshold` and
`confidenceThreshold` are model-specific and must travel with the model.
Local config: `predictionTtlMs` (10 000), `telemetryFreshnessMs` (2 000), `heartbeatIntervalMs`
(3 000), `authorizationWatermarkTimeoutMs` (90 000), `fallbackRecoveryHoldMs` (30 000),
`clockSkewBudgetMs` (250).

## 16. Security

mTLS client identity to Control Service. **No equipment credentials** (`SECURITY_BOUNDARIES.md`) —
enforced by provisioning, not merely by convention: the Supervisor's environment contains no OT
endpoint credentials at all.

## 17. Observability

`ai_recommendation_total{decision,reason}`, `safety_gate_result_total{gate,passed}`,
`fallback_active{equipment}` (gauge, low cardinality via equipment count), `recommendation_age_seconds`,
`command_intents_total`, `prediction_record_age_seconds{partition}`,
`model_authorization_age_seconds`, `lease_valid`, `decision_latency_seconds`.

## 18. Performance targets (TARGET — unmeasured)

| Metric | Target |
|---|---|
| P95 decision latency (prediction receipt → decision published) | ≤ 250 ms |
| P99 | ≤ 500 ms |
| Gate evaluation | ≤ 50 ms |
| Throughput | ≥ 50 decisions/s |

## 19. Test strategy

**Table-driven tests over every one of the 13 gates and their combinations** — required by
`TEST_STRATEGY.md`. Default outcome for any ambiguous or invalid required input is
**rejection/fallback, never acceptance**.
Failure: kill inference (AC-004); OOD injection (AC-005); stale telemetry; quarantine propagation;
watermark timeout; epoch fence; restart with backlog.
Property: no gate combination yields `ACCEPT` unless **all 13** pass.

## 20. Acceptance criteria

AC-004, AC-005, AC-006, AC-011, AC-013, AC-015, AC-016, AC-017.
