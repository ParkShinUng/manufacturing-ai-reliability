# Glossary

## Core concepts (v0.1–v0.2)

- **AI Recommendation**: non-authoritative proposed operating target generated from inference.
- **Safety Supervisor**: deterministic application layer that validates recommendations and applies
  fallback policy. **Not a certified SIS.**
- **Fallback**: documented non-AI operating behaviour used when recommendation acceptance conditions
  fail.
- **Telemetry Event**: normalized equipment measurement event.
- **Fault Injection**: intentional simulator-induced degradation/failure for verification.
- **OOD**: out-of-distribution input relative to the model's training/validation domain. In v0.3 an
  OOD gate failure is a **hard reject**, never reduced authority (ADR-0015).
- **Freshness**: allowed maximum age of telemetry or recommendation before it becomes invalid.
- **Idempotency Key**: stable key ensuring repeated command delivery does not repeat the effect.
  Scope is **transport retry of one decision**, not logical re-decision.
- **Digital Twin/HIL**: behavioural equipment simulator used for protocol, fault, and control
  validation; not necessarily a 3D twin.

## Authority and control (v0.3)

- **L1 / L2 / L3**: the three authority layers (ADR-0011). L1 = Safety Supervisor, advisory only.
  L2 = Control Service, the **sole application writer** to equipment, owner of control mode and the
  fallback watchdog. L3 = the equipment itself, holding protective trips and a dead-man revert.
  L3 survives total platform loss.
- **`controlEpoch`**: a durable, strictly monotonic per-equipment counter owned by the Control
  Service. It is a **fencing token**: a command carrying an epoch lower than the persisted one is
  rejected with `COMMAND_EPOCH_STALE`. It exists because a command issued *before* a watchdog
  fallback and delayed in the network would otherwise arrive still unexpired and undo the fallback.
- **Fencing**: rejecting an action taken under superseded authority. Distinct from *expiry* (too
  old) and from *supersession* (a newer intent exists).
- **Control lease**: the grant a Supervisor must hold, via `AcquireControlLease`, before the Control
  Service will accept its commands. Acquiring a lease increments `controlEpoch`.
- **Fallback watchdog**: the Control Service timer that drives equipment to the configured fallback
  rate after 12 s of Supervisor silence, autonomously and without AI logic.
- **Dead-man**: the equipment's own revert to a safe default rate when no setpoint refresh arrives
  within 30 s. The only mechanism that survives the entire platform being down.
- **Control mode**: per-equipment answer to "what may currently determine the setpoint?" — one of
  `NORMAL_RULE`, `AI_ASSISTED`, `SAFE_FALLBACK`, `STOP_REQUIRED`. **Owned by the Control Service**,
  not the Supervisor, so that mode is defined when the Supervisor is gone.
- **Structural non-concurrency**: the ordering guarantee of ADR-0013 — command validity (2 s) is
  shorter than the inference cadence (5 s), so two commands for one equipment can never be
  simultaneously valid. Replaces both a distributed counter and wall-clock ordering.

## Data quality and time (v0.3)

- **Safety-required vs advisory channel**: a sensor whose loss forces `quality.overall = BAD` and
  blocks AI, versus one whose loss only degrades to `UNCERTAIN`. Mapping in
  `EQUIPMENT_MODEL_AND_STATE.md` §4.
- **`quality.overall`**: `GOOD` / `UNCERTAIN` / `BAD`, **derived by the Edge Gateway** from the
  closed quality-flag vocabulary and the sensor map. Never free-form.
- **Null measurement**: the representation of a sensor with no trustworthy value. Must carry a
  quality flag. **Substituting a synthetic, zero, or last-known value is forbidden** — a fabricated
  reading would pass the safety gates (ADR-0018).
- **`validSampleRatio`**: valid samples ÷ **expected** samples for a channel in a feature window.
  The denominator derives from window duration × cadence, not from records received, so a
  mid-window restart cannot change it.
- **Skew budget**: ±250 ms tolerated clock difference between platform hosts, added on the
  permissive side of freshness comparisons.
- **`sequence` vs `stateSequence`**: telemetry `sequence` is assigned by the **equipment** (so
  gateway-side loss is detectable); `stateSequence` is assigned by the **gateway**. Named distinctly
  so they cannot be confused.

## MLOps (v0.3)

- **`deploymentStage`**: `SHADOW` / `CANARY` / `PRODUCTION`, carried on every prediction. Without it
  a shadow model's output is indistinguishable from a production model's at the decision point.
- **`ModelAuthorization`**: the record on the compacted topic `factory.model-deployments.v1` that
  grants or revokes a model's authority. The Supervisor consumes it and **never calls MLflow on the
  decision path**.
- **`AuthorizationWatermark`**: a liveness record emitted every 30 s even when nothing changes.
  It exists because a dead publisher leaves Kafka reachable and simply **quiet**, and silence would
  otherwise be indistinguishable from "nothing changed."
- **Quarantine**: marking a model as not authorized. Propagates in ~2 s via Kafka.

## Process (v0.3)

- **Decision Record (DEC)**: record of how a decision was *challenged* by the independent agent.
  Complements an ADR, which records what was *decided*.
- **`REJECTED_WITH_EVIDENCE`**: a classification in the dual-agent protocol — a challenge considered
  and declined with stated reasoning, as opposed to ignored.
- **Implementation gate**: the state in `docs/10-human-review/<version>/HUMAN_APPROVAL.md`.
  AI agreement does not open it; only a human does.
- **`TARGET (unmeasured)`**: a performance figure that is a hypothesis. It may not be presented as a
  result until a reproducible report exists under `reports/`.
- **`TBD-WITH-DECISION`**: a value deliberately left open because it requires future evidence,
  distinguished from a value simply forgotten.
