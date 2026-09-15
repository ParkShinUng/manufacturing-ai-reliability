# Service Design — MLflow / Model Lifecycle + `mlops-publisher`

> Implements DEC-009. `mlops-publisher` is **new in v0.3** and exists to remove MLflow from the
> safety path.

## 1. Purpose
Own model lineage, stage transitions, and quarantine — and **publish authorization to Kafka** so the
Safety Supervisor never calls MLflow on a decision.

## 2. Responsibilities
**MLflow:** experiments, artifacts, registry stages, lineage (ADR-0008).
**`mlops-publisher`:** translate registry transitions into `ModelAuthorization` events on
`factory.model-deployments.v1`; emit an `AuthorizationWatermark` every 30 s; guarantee durable
publication of a quarantine **before** the registry transition is reported complete.

## 3. Non-responsibilities
Neither component is on the command path. Neither evaluates safety gates. MLflow is **never** queried
by the Supervisor.

## 4. Dependencies
| Dependency | Class | Failure behaviour |
|---|---|---|
| MLflow ← publisher | non-control-critical | publisher retries; last published authorization stands |
| Kafka ← publisher | **control-relevant** | quarantine cannot propagate → watermark stops → Supervisor fails closed |

## 5. Inputs / outputs
In: MLflow registry transitions. Out: `factory.model-deployments.v1`.

## 6. Contracts
`contracts/jsonschema/v1/model-authorization.schema.json`.

## 7. Data ownership
MLflow owns lineage and stage. The **Kafka compacted topic is the authoritative runtime source** for
the Supervisor — this split is the whole point of DEC-009.

## 8. State model
Publisher: last published `authorizationSequence` per model, last watermark time. Rebuildable from
MLflow plus the compacted topic.

## 9. Lifecycle
`Experiment → Registered → Candidate → Shadow → Canary → Production → Archived/Quarantined`.
Each transition emits an `AUTHORIZATION` record.

## 10. Normal flow
```text
on registry transition:
  1  build ModelAuthorization (stage, thresholds, featureSchemaVersion, cohort, quarantined)
  2  authorizationSequence += 1
  3  produce with acks=all and AWAIT the ack        <-- durability before reporting success
  4  only then report the registry transition complete
every 30s:
  emit AuthorizationWatermark (even when nothing changed)
```
Step 3's ordering is what prevents the authoritative record from lagging the registry.

## 11. Failure behaviour — the silence problem
Codex identified the remaining fail-open: if the publisher dies, Kafka stays reachable and simply
**quiet**, and silence is indistinguishable from "nothing changed", so a connection-staleness bound
never trips.

| Failure | Behaviour |
|---|---|
| Publisher down | no watermark → Supervisor sees age > 90 s → **all models unauthorized** → fallback |
| Kafka down for publisher | quarantine cannot publish; registry transition **not** reported complete |
| MLflow down | publisher retries; last published authorization remains in force |
| Watermark never seen at startup | Supervisor treats all models unauthorized until the first watermark |

The watermark converts an undetectable silent failure into a detectable one. That was the actual
defect, and it is why the watermark exists at all.

## 12. Timeout / retry / idempotency / ordering
MLflow poll/webhook 10 s timeout, 3 attempts, 1→4 s backoff, breaker after 3 for 60 s.
Publication idempotent by `(modelName, modelVersion, authorizationSequence)`.
Compacted topic key = `modelName`, so the newest authorization per model always survives compaction.

## 13. Backpressure
Low volume; bounded buffer of 100; a full buffer is an alert, not a drop — losing an authorization
event is never acceptable.

## 14. Restart recovery
Rebuild from MLflow, re-publish current authorization for every model, resume watermarks.

## 15. Configuration
`watermarkIntervalMs` (30 000), `watermarkTimeoutMs` (90 000, consumed by the Supervisor),
MLflow URI, promotion thresholds.

## 16. Security
Publisher has MLflow read + Kafka write on one topic. No equipment credentials. Stage transitions
require a human-recorded approver.

## 17. Observability
`model_authorizations_published_total{stage}`, `authorization_watermark_age_seconds`,
`quarantine_propagation_latency_seconds`, `mlflow_errors_total`.

## 18. Performance targets (TARGET — unmeasured)
Quarantine propagation (registry → Supervisor effective) P95 ≤ **2 s**; watermark jitter ≤ 5 s.

## 19. Test strategy
**Quarantine propagation test**: quarantine a model, assert the Supervisor rejects its predictions
within 2 s. **Publisher-death test**: kill the publisher, assert the Supervisor falls back within
90 s. Rollback demo (AC-010). Compaction retains the newest authorization per model.

## 20. Acceptance criteria
AC-008, AC-010, AC-033, AC-034.
