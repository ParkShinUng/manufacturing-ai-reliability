# Service Design — `prediction-service`

> Runtime: Python 3.13. Consumes features, emits non-authoritative predictions.

## 1. Purpose
Run approved models over feature windows and publish predictions. **It has no control authority and
no equipment credentials** (ADR-0002) — its output is advisory input to the Safety Supervisor.

## 2. Responsibilities
Build 60 s feature windows from telemetry; load authorized models; infer every 5 s per equipment;
emit `factory.predictions.v1` with model identity, `deploymentStage`, `featureSchemaVersion`, and
`validSampleRatio`; run shadow candidates in a separate consumer group (ADR-0010).

## 3. Non-responsibilities
No safety gates; no commands; no equipment access; does not decide its own deployment stage; does
**not** emit a prediction when the feature window is insufficient.

## 4. Dependencies
| Dependency | Class | Failure behaviour |
|---|---|---|
| Kafka telemetry | Non-control-critical | no features → no predictions → Supervisor falls back |
| MLflow (model artifact load) | Non-control-critical, **startup only** | cached artifact; already-loaded model keeps serving (ADR-0008) |
| Kafka predictions (produce) | Non-control-critical | buffer |

## 5. Inputs / outputs
**In:** `factory.telemetry.v1`. **Out:** `factory.predictions.v1`.

## 6. Contracts
`contracts/jsonschema/v1/telemetry.schema.json`, `prediction.schema.json`.

## 7. Data ownership
Owns feature computation and model artifacts in memory. `mair_ml_core` owns feature definitions
shared with training — **the single most important anti-skew measure in the platform**.

## 8. State model
Per equipment: rolling 60 s window of 1 s aggregates, `validSampleRatio`, last inference time.
Global: loaded model, version, stage, feature schema version.

## 9. Lifecycle
Resolve authorized model → load artifact → warm up → subscribe telemetry → fill windows → begin
inference once a window is ≥ 80 % complete on every safety-required channel.

## 10. Normal flow
```text
per 1s bucket:  aggregate telemetry by EVENT TIME into absolute second boundaries
                nulls EXCLUDED from statistics, never zero-filled
                validSampleRatio = valid / EXPECTED (duration x cadence), not / received
per 5s:         if all safety-required validSampleRatio >= 0.8:
                    infer -> anomaly, failure prob, RUL, confidence, OOD
                    emit prediction with monotonically increasing predictedAtUtc
                else:
                    emit NOTHING  (FEATURE_WINDOW_INSUFFICIENT)
```
Two details carry the design: event-time absolute boundaries make aggregation restart-invariant, and
the expected-count denominator prevents a mid-window restart from changing the ratio.

## 11. Failure behaviour
| Failure | Behaviour |
|---|---|
| Insufficient window | **no prediction at all** — a prediction from a half-empty window would carry a confidence the model was never trained to produce |
| Model load failure | service unhealthy; does **not** serve with a stale or fallback model |
| Inference timeout > 500 ms | skip this cycle, count it |
| MLflow down | already-loaded model continues (ADR-0008) |
| Kafka down | buffer; on recovery emit only fresh predictions |

## 12. Timeout / retry / idempotency / ordering
Inference budget 500 ms. `predictedAtUtc` **strictly increasing per equipment** — required by
DEC-003 supersession; enforced by a per-equipment monotonic guard that clamps forward if the wall
clock steps backwards. Offsets committed after emit. Duplicate identity `predictionId`.

## 13. Backpressure
Bounded window buffers; if telemetry outpaces aggregation, drop oldest raw samples and lower
`validSampleRatio` honestly rather than silently interpolating.

## 14. Restart recovery
Windows rebuild from telemetry retention. No prediction is emitted until windows are ≥ 80 % complete,
so a restart yields a clean gap rather than degraded output.

## 15. Configuration
`inferenceCadenceSec` (5), `featureWindowSec` (60), `aggregateBucketSec` (1),
`minValidSampleRatio` (0.8), `inferenceTimeoutMs` (500), `modelName`, `deploymentStage`.

## 16. Security
**No equipment credentials.** Read-only MLflow. Runs in the Data/AI zone (`SECURITY_BOUNDARIES.md`).

## 17. Observability
`inference_requests_total`, `inference_errors_total`, `inference_latency_seconds`,
`prediction_ood_total`, `model_version_info`, `feature_window_insufficient_total`,
`valid_sample_ratio` (histogram), `predictions_emitted_total{stage}`.

## 18. Performance targets (TARGET — unmeasured)
P95 inference ≤ 300 ms, P99 ≤ 500 ms; 250 equipment @ 5 s = 50 inferences/s;
feature build P95 ≤ 100 ms.

## 19. Test strategy
Unit: aggregation determinism with nulls; `validSampleRatio`; monotonic `predictedAtUtc` across a
simulated backward clock step. Contract: every emitted prediction validates against the schema.
Integration: telemetry → features → prediction. Model-quality tests are kept **separate** from
system-safety tests (`TEST_STRATEGY.md`).

## 20. Acceptance criteria
AC-005, AC-008, AC-024, AC-025.
