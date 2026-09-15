# Observability and SLO

> v0.3 adds the metrics **contract** — name, type, unit, labels, owner (GAP-082) — and resolves the
> v0.2 conflict between "avoid high-cardinality labels" and the need for per-equipment correlation
> (GAP-043). Numeric targets are **derived from** `NON_FUNCTIONAL_REQUIREMENTS.md` §2, which is the
> single normative source; this document does not restate them independently.

## 1. The cardinality rule

v0.2 said "avoid high-cardinality metric labels" while also requiring per-equipment traceability, and
never resolved the tension. The rule is now explicit:

> **`equipmentId` MUST NOT appear as a metric label.** It belongs in traces, logs, and exemplars.
> Metrics are aggregate; traces are per-entity.

With 250 equipment, an `equipmentId` label multiplies every series by 250 and the reason-code label
set on top of that. Allowed label values are enumerated per metric below, and every one is a bounded
enum. `detail` fields on quality flags are **never** labels.

The one deliberate exception is `fallback_active`, a gauge that must be per-equipment to be
actionable; it is bounded by equipment count and carries no second dimension.

## 2. Metrics contract

Type: C = counter, G = gauge, H = histogram.

### 2.1 Edge Gateway

| Metric | T | Unit | Labels | Purpose |
|---|---|---|---|---|
| `protocol_connected` | G | bool | `protocol` | OT session health |
| `telemetry_events_total` | C | events | `protocol` | ingest volume |
| `telemetry_publish_errors_total` | C | errors | `reason` | producer health |
| `telemetry_dropped_total` | C | events | — | **bounded loss (D-02)** |
| `telemetry_sequence_gaps_total` | C | gaps | — | **undetected loss must stay zero (D-03)** |
| `local_buffer_depth` | G | events | — | backpressure |
| `reconnect_total` | C | reconnects | `protocol` | R-01 |
| `quality_flags_total` | C | flags | `flag`, `channel` | closed enums, 12 × 8 bounded |
| `clock_skew_seconds` | G | s | — | D-06 |

### 2.2 Kafka consumers

| Metric | T | Unit | Labels |
|---|---|---|---|
| `kafka_consumer_lag` | G | records | `group`, `topic`, `partition` |
| `prediction_record_age_seconds` | G | s | `partition` |
| `processing_errors_total` | C | errors | `group`, `reason` |
| `dlq_messages_total` | C | records | `topic`, `reason` |
| `replay_total` | C | runs | `group` |

`prediction_record_age_seconds` exists because offset lag can look healthy in aggregate while one
partition starves — age is the signal that actually maps to the prediction TTL.

### 2.3 Prediction service

| Metric | T | Unit | Labels |
|---|---|---|---|
| `inference_requests_total` | C | requests | `stage` |
| `inference_errors_total` | C | errors | `reason` |
| `inference_latency_seconds` | H | s | `stage` |
| `prediction_ood_total` | C | predictions | — |
| `feature_window_insufficient_total` | C | windows | `channel` |
| `valid_sample_ratio` | H | ratio | `channel` |
| `model_version_info` | G | 1 | `model`, `version`, `stage` |

`model_version_info` is the one intentional info-gauge; its cardinality grows only on deployment and
old series are dropped on restart.

### 2.4 Safety Supervisor

| Metric | T | Unit | Labels |
|---|---|---|---|
| `ai_recommendation_total` | C | decisions | `decision`, `reason` |
| `safety_gate_result_total` | C | evaluations | `gate`, `passed` |
| `fallback_active` | G | bool | `equipment` *(the one exception, §1)* |
| `recommendation_age_seconds` | H | s | — |
| `command_intents_total` | C | intents | — |
| `model_authorization_age_seconds` | G | s | — |
| `authorization_watermark_age_seconds` | G | s | — |
| `lease_valid` | G | bool | — |
| `decision_latency_seconds` | H | s | — |

### 2.5 Control Service

| Metric | T | Unit | Labels |
|---|---|---|---|
| `commands_total` | C | commands | `status`, `reason` |
| `command_latency_seconds` | H | s | — |
| `duplicate_command_total` | C | commands | — |
| `command_epoch_stale_total` | C | commands | — |
| `command_superseded_total` | C | commands | — |
| `watchdog_fallback_total` | C | events | — |
| `control_mode` | G | enum | `mode` |
| `control_epoch` | G | count | — |
| `rate_budget_remaining_pp` | G | pp | — |
| `ot_write_errors_total` | C | errors | `reason` |

### 2.6 Equipment simulator

`simulator_loop_overruns_total` (C), `equipment_state` (G, label `state`),
`fault_injections_total` (C, label `profile`), `deadman_reverts_total` (C),
`protective_trips_total` (C, label `condition`).

### 2.7 Platform

`up{job}` (G), `http_requests_total` (C, `route`/`status`), `http_request_duration_seconds` (H),
`projection_lag_seconds` (G), `db_query_latency_seconds` (H).

## 3. Alert rules

| Alert | Condition | Severity |
|---|---|---|
| `SupervisorSilent` | `watchdog_fallback_total` increases | **CRIT** |
| `AuthorizationStale` | `authorization_watermark_age_seconds > 90` | **CRIT** |
| `PredictionsStale` | `prediction_record_age_seconds > 30` | **CRIT** |
| `PredictionsLagging` | `prediction_record_age_seconds > 10` for 1 min | WARN |
| `SequenceGapDetected` | `telemetry_sequence_gaps_total` increases | WARN |
| `TelemetryDropping` | `telemetry_dropped_total` increases | WARN |
| `ProtocolDisconnected` | `protocol_connected == 0` for 30 s | WARN |
| `FallbackSustained` | `fallback_active == 1` for 5 min | WARN |
| `ClockSkewHigh` | `clock_skew_seconds > 0.25` | WARN |
| `DlqGrowing` | `dlq_messages_total` increases | WARN |
| `CommandsFencedRepeatedly` | `command_epoch_stale_total` > 5 in 5 min | WARN |
| `OtWriteFailing` | `ot_write_errors_total` > 3 in 1 min | **CRIT** |

`CommandsFencedRepeatedly` is worth calling out: a single fenced command is normal after a watchdog
event, but a repeating pattern means a Supervisor is failing to re-acquire its lease correctly.

## 4. SLO targets

All derived from `NON_FUNCTIONAL_REQUIREMENTS.md` §2 — see A-01…A-04, L-01…L-10, R-01…R-08, D-01…D-07
there. **Every value is `TARGET (unmeasured)`** until a report exists under `reports/`.
Measurement window for availability SLOs: the duration of the named failure test, recorded in the
report, not a rolling production window (this is a portfolio platform, not a production service, and
claiming a production SLO would be dishonest).

## 5. Tracing

| Aspect | Rule |
|---|---|
| Propagation | `correlationId` (W3C `traceparent`) across every service boundary |
| Sampling: control decisions | **100 %** |
| Sampling: 100 ms telemetry path | **1 %** |
| Span attributes | `equipmentId`, `predictionId`, `decisionId`, `commandId`, `controlEpoch` |
| Exporter | bounded queue, **drop on full, never block** |

The exporter rule is a safety property, not a performance one: a blocking trace exporter would turn
an observability outage into a control outage (F32).

Required end-to-end trace: `telemetry → feature window → prediction → safety decision → command →
outcome`, verified by AC-041.

## 6. Dashboards

1. **Equipment** — state, mode, rate (commanded vs applied), sensor values, quality flags.
2. **AI** — anomaly/RUL, confidence, OOD, model version and stage, acceptance vs rejection by reason.
3. **Safety and control** — gate pass/fail, fallback rate, command outcomes, epoch, rate budget.
4. **Platform** — lag, record age, DLQ, error rates, clock skew, watermark age.

Dashboard JSON lives in `observability/grafana/` and is version-controlled and diffable, not clicked
together in a UI.
