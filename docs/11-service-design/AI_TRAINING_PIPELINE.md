# Service Design — `training` (AI Training Pipeline)

> Runtime: Python 3.13 batch jobs. **Not part of the control runtime.**

## 1. Purpose
Produce reproducible datasets and model artifacts, and register them in MLflow with full lineage
(FR-040, AC-008).

## 2. Responsibilities
Generate/extract datasets from replayed telemetry with ground-truth labels; compute features via
`mair_ml_core` (**the same code the inference service uses**); train anomaly and failure/RUL models;
evaluate against promotion gates; log runs, params, metrics, artifacts, dataset ID, feature schema
version, and git commit to MLflow.

## 3. Non-responsibilities
No serving; no control; no direct promotion to Production (that is a human decision recorded in the
registry and published by `mlops-publisher`).

## 4. Dependencies
Kafka/object store (datasets), MLflow (registry) — both **non-control-critical**; a training outage
has zero effect on running control.

## 5. Inputs / outputs
In: replayed telemetry, fault-injection ground truth (`T_fail` from the simulator metadata).
Out: MLflow run, model artifact, metrics, dataset version.

## 6. Contracts
Feature schema version (shared with `prediction-service`); model artifact signature;
`model-authorization.schema.json` fields that promotion later populates.

## 7. Data ownership
Owns datasets and model artifacts. Ground truth originates in the simulator
(`EQUIPMENT_MODEL_AND_STATE.md` §2.1) — training only records it, never invents it.

## 8. State model
Stateless batch. All state in MLflow and the artifact store.

## 9. Lifecycle
Dataset build → feature build → train → evaluate → log to MLflow → register as Candidate. Promotion
beyond Candidate is a separate, human-approved step.

## 10. Normal flow
Replay a telemetry range → label from fault-injection metadata → build features with the **identical**
`mair_ml_core` code path as serving → train with a fixed seed → evaluate on a held-out set →
log everything → register Candidate.

## 11. Failure behaviour
| Failure | Behaviour |
|---|---|
| MLflow down | job fails and is retried; **no partial registration** |
| Insufficient data | job fails explicitly rather than training on a thin dataset |
| Promotion gate not met | Candidate remains Candidate; never auto-promoted |
| Non-deterministic result under fixed seed | **build failure** — reproducibility is a requirement (NFR-005), not a nicety |

## 12. Timeout / retry / idempotency / ordering
Jobs are idempotent by dataset version + config hash: re-running with identical inputs produces an
identical artifact and is detected rather than duplicated.

## 13. Backpressure
Batch; concurrency limited by the runner.

## 14. Restart recovery
Re-run from the dataset version; no partial state survives.

## 15. Configuration
`datasetVersion`, `featureSchemaVersion`, `seed`, model hyperparameters, promotion thresholds.

## 16. Security
Read-only telemetry access. **No equipment credentials.** No production write path.

## 17. Observability
`training_runs_total{status}`, `training_duration_seconds`, evaluation metrics logged to MLflow,
`dataset_rows`.

## 18. Performance targets (TARGET — unmeasured)
Full training on a 24 h dataset ≤ 30 min on the reference machine; reproducible to identical metrics
under a fixed seed.

## 19. Test strategy
Determinism under a fixed seed; **training/serving feature parity test** — the same input through
`mair_ml_core` must yield byte-identical features in both paths; promotion-gate evaluation logic;
MLflow lineage completeness (AC-008).

## 20. Acceptance criteria
AC-008, AC-032.
