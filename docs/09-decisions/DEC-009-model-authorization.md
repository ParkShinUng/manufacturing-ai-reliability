# DEC-009 — Model authorization during a registry outage

## Status
CONVERGED (3 rounds) — Human review **APPROVED** (HD-001, 2026-09-15)

## Problem
ADR-0008 requires graceful degradation when MLflow is unavailable. But if a model is quarantined
**during** an outage, a degraded Supervisor keeps trusting it — fail-open in the one direction that
matters. `MODEL_QUARANTINED` was a safety gate whose input could silently go stale.

## Relevant requirements
FR-041, FR-042, FR-043, AC-010. Gaps GAP-070, GAP-071.

## Related ADRs
ADR-0008, ADR-0010. New: ADR-0019.

## Claude initial proposal (Round 1)
Supervisor caches a `ModelAuthorization` record with a hard `max_staleness` of 300 s; beyond that the
model is treated as unauthorized.

## Codex challenge (Round 1) — **NEEDS_HUMAN_DECISION**
"A 300 000 ms staleness bound means a quarantined model can keep influencing equipment for up to
5 minutes during an MLflow outage… That may be acceptable for a portfolio simulator, but it is a
human risk-tolerance decision, not an engineering default."
Also: "**Hidden synchronous safety dependency on MLflow freshness**, contrary to
`SYSTEM_ARCHITECTURE.md` saying local safe-control must not depend synchronously on MLflow."
Simpler alternative: fail closed immediately when quarantine state cannot be refreshed.

## Claude response (Round 2)
**Classification: PARTIALLY_ACCEPTED.** Codex identified a real contradiction — but both of its
options inherit the same flawed premise, namely **polling MLflow from the safety path**. Caching it
hides the dependency; failing closed immediately makes it explicit. Neither removes it.

**Revision: publish authorization, do not poll it.** `factory.model-deployments.v1` is already a
compacted, infinite-retention topic. An `mlops-publisher` translates registry transitions into
`ModelAuthorization` events; the Supervisor consumes them and **never calls MLflow on the decision
path**. Quarantine propagates in milliseconds; an MLflow outage has **zero** effect.

## Codex re-evaluation (Round 2) — **NEW_PROBLEM_INTRODUCED**
"Moving authorization to `factory.model-deployments.v1` removes Supervisor-to-MLflow polling, but
introduces `mlops-publisher` as an undocumented safety-critical propagation component. **If
quarantine occurs while `mlops-publisher` is down, Kafka remains reachable and quiet, so the
Supervisor never trips the proposed Kafka staleness bound.**"

This is the sharpest finding of the review: **silence is indistinguishable from "nothing changed."**

## Claude resolution (Round 3) — ACCEPTED
Codex is right, and the fix is to make silence detectable:

- `mlops-publisher` emits an **`AuthorizationWatermark`** every **30 s** even when nothing has
  changed.
- The Supervisor tracks watermark age. Age > **90 s** (3 missed) ⇒ **all models treated as
  unauthorized** ⇒ `MODEL_AUTHORIZATION_STALE` ⇒ fallback.
- No watermark ever seen at startup ⇒ unauthorized until the first arrives.
- A quarantine must be durably produced with `acks=all` **before** the MLflow registry transition is
  reported complete, so the authoritative record cannot lag the registry.

The watermark converts an undetectable silent failure into a detectable one, which was the actual
defect. `mlops-publisher` is documented as a safety-relevant component in
`docs/11-service-design/MODEL_LIFECYCLE.md` with its own failure row (F28) and NFR-012 rationale.

## Alternatives considered
| Option | Verdict |
|---|---|
| Cache with 300 s staleness | rejected — 5-minute window for a quarantined model to retain authority |
| Fail closed immediately on registry unreachability | rejected — makes MLflow an explicit synchronous safety dependency, forbidden by `SYSTEM_ARCHITECTURE.md` |
| **Publish to compacted Kafka + liveness watermark** | **adopted** |

## Evidence
The Kafka path propagates quarantine in ~ms versus a polling interval, and the watermark closes the
silent-publisher hole. This satisfies ADR-0008's "degrade gracefully" and
`SYSTEM_ARCHITECTURE.md`'s "no synchronous MLflow dependency" **simultaneously**, which neither
Round-1 option did.

## Trade-offs
Adds one component (`mlops-publisher`). Justified under NFR-012: it exists to remove a synchronous
safety dependency, and its own failure is detectable and fails closed.

## Residual open question
If the Supervisor cannot reach **Kafka**, how long may it honour its last-known authorization?
Proposed backstop: **60 s** (reduced from 300 s because the Kafka path is far faster than polling).

## Final decision
Authorization published to a compacted Kafka topic; 30 s watermark; 90 s watermark timeout;
60 s backstop staleness bound **pending human approval**.

## Consensus
Claude: **ACCEPT** · Codex: **ACCEPT** on the mechanism; **escalate** the numeric bound

## Human review

**APPROVED (2026-09-15).** HD-001 resolved: `maxAuthorizationStalenessMs` = **60 000 ms**. The
proposed value was accepted without modification. Recorded in
`docs/10-human-review/v0.3/HUMAN_APPROVAL.md`.

Scope note: v0.3 is approved. `maxAuthorizationStalenessMs` = 60 000 ms is a decided value;
changing it requires a new human decision, not an engineering judgement.

## Related ADR
ADR-0019.
