# ADR-0019 — Model authorization published to a compacted topic with a liveness watermark
Status: Accepted (v0.3, dual-agent reviewed)
Decision Record: [DEC-009-model-authorization](../09-decisions/DEC-009-model-authorization.md)

## Context
ADR-0008 required graceful degradation when MLflow is unavailable, but a model quarantined during an outage would continue to be trusted - fail-open in the only direction that matters. Caching the registry state hid a synchronous safety dependency; failing closed on registry unreachability would have made that dependency explicit rather than removing it.

## Decision drivers
- ADR-0008: degrade gracefully when MLflow is unavailable.
- `SYSTEM_ARCHITECTURE.md`: safe control must not depend synchronously on MLflow.
- A quarantine must not be silently lost.
- NFR-012: a new component requires a documented requirement.

## Considered options
1. Cache the registry state with a 300 s staleness bound.
2. Fail closed immediately when the registry is unreachable.
3. Publish authorization to a compacted topic with a liveness watermark.

## Decision
`mlops-publisher` translates MLflow registry transitions into `ModelAuthorization` events on the compacted, infinite-retention topic `factory.model-deployments.v1`. The Safety Supervisor consumes that topic and never calls MLflow on the decision path. The publisher emits an `AuthorizationWatermark` every 30 s even when nothing changes; a watermark older than 90 s causes the Supervisor to treat all models as unauthorized. A quarantine must be durably produced with `acks=all` before the registry transition is reported complete.

## Rationale
Option 3. Options 1 and 2 share a flawed premise - polling MLflow from the safety path - and differ only in whether the dependency is hidden or explicit. Publishing removes it: quarantine propagates in milliseconds and an MLflow outage has zero effect on the Supervisor. The watermark was added after Codex observed that a dead publisher leaves Kafka reachable and simply *quiet*, so silence would otherwise be indistinguishable from 'nothing changed' and a connection-staleness bound would never trip.

## Trade-offs
Adds one component. Justified under NFR-012 because it exists specifically to remove a synchronous safety dependency, and its own failure is detectable and fails closed.

## Reliability impact
Quarantine propagation moves from a polling interval to ~milliseconds, and publisher death becomes detectable within 90 s.

## Failure impact
F27 (MLflow unavailable - no effect) and F28 (publisher down - fail closed) in `FAILURE_MODEL.md`.

## Operational impact
`authorization_watermark_age_seconds` is a first-class alert. Operators can distinguish 'nothing changed' from 'nobody is telling us'.

## Security impact
Prevents a quarantined model retaining control authority through an infrastructure outage.

## Alternatives rejected
Both polling-based options were rejected because neither removes the forbidden synchronous dependency; the disagreement was about where to put the risk, not whether to accept it.
