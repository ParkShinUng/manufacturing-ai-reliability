# Definition of Ready

> Companion to `DEFINITION_OF_DONE.md`. DoD says when work may be **closed**; DoR says when work may
> be **started**. v0.2 had only the former, which is how a specification can look complete while
> still forcing an implementer to invent architecture.

A feature, phase, or change is **ready for implementation** only when **every** line below is true.

## 1. Requirement

- [ ] A requirement ID exists (`FR-nnn` or `NFR-nnn`) and states observable behaviour, not intent.
- [ ] Priority is assigned (P0 / P1) and justified against the portfolio goal.
- [ ] The requirement is **falsifiable** — it is possible to state what would prove it unmet.

## 2. Architecture

- [ ] The owning service is named, and its **non-responsibilities** are stated as explicitly as its
      responsibilities.
- [ ] Every dependency is classified **control-critical / control-independent**.
- [ ] Data ownership is unambiguous: exactly one component owns each piece of state.
- [ ] **Control ownership** is unambiguous where equipment can move.
- [ ] If the change touches authority, failure isolation, or a trust boundary, a Decision Record
      exists and is `CONVERGED` or `HUMAN_DECISION_REQUIRED`-and-approved.

## 3. Decision and ADR

- [ ] Every architecture-significant choice has an **accepted ADR**.
- [ ] Any choice that was challenged has a Decision Record showing the challenge and its resolution.
- [ ] No `OPEN DECISION` marker remains in a document the work depends on.
- [ ] Deferred options are recorded as deferred, not silently dropped.

## 4. Contract

- [ ] A machine-readable contract exists where one is needed (JSON Schema / Protobuf / OpenAPI).
- [ ] The contract is versioned, and compatibility rules are stated.
- [ ] Every documented example is a **validated instance**, proven by `validate_examples.mjs`.
- [ ] Enum values, units, and timestamp semantics are explicit — no untyped strings carrying meaning.
- [ ] Duplicate identity is defined for every event the work produces or consumes.

## 5. Failure behaviour

- [ ] Every new failure mode has a row in `FAILURE_MODEL.md` with its **CONTROL-CRITICAL /
      CONTROL-INDEPENDENT** classification.
- [ ] Timeout, retry, backoff, and circuit-breaker values are **numeric**, not "bounded".
- [ ] Idempotency and ordering semantics are stated, including their **scope limits**.
- [ ] Restart recovery is defined, including what is deliberately *not* restored.
- [ ] The **fail direction is closed**: where a safety-relevant input cannot be validated, the
      system degrades to a more conservative state (NFR-014).

## 6. Acceptance and test

- [ ] At least one `AC-nnn` maps to the requirement, and it is testable without human judgement.
- [ ] A test specification exists in `TEST_SPECIFICATIONS.md` with setup, trigger, expected
      transitions, thresholds, and pass/fail assertions.
- [ ] Any performance claim is labelled `TARGET (unmeasured)` until a report exists.

## 7. Observability

- [ ] Metrics are named with type, unit, and an **enumerated, bounded** label set.
- [ ] No metric carries `equipmentId` as a label.
- [ ] Trace propagation is defined where the work crosses a service boundary.

## 8. Security

- [ ] The authentication mechanism is **named** for every new hop — not "credentials".
- [ ] Authorization is derived from verified identity, never from a self-asserted payload field.
- [ ] Nothing new is audit-relevant without an audit record defined for it.

## 9. Gate

- [ ] **P0 architecture issues: 0.**
- [ ] **Unresolved P1 architecture issues: 0.**
- [ ] All `HUMAN_DECISION_REQUIRED` items affecting this work are **APPROVED**.
- [ ] `docs/10-human-review/<version>/HUMAN_APPROVAL.md` permits implementation.

---

## Why a checklist rather than judgement

Each line above corresponds to a defect actually found in the v0.2 → v0.3 review:

| Line | The finding it would have caught |
|---|---|
| 2 — non-responsibilities stated | Control mode had no owner, so it was undefined when the Supervisor died |
| 2 — control ownership | "Control survives AI failure" was true only for inference death |
| 4 — validated examples | The telemetry example omitted three schema-required fields |
| 4 — no untyped strings | `source` was a self-asserted string doing access control |
| 5 — numeric values | "bounded retry" appeared with no number anywhere |
| 5 — fail direction | A quarantined model kept authority through a registry outage |
| 6 — testable AC | AC-001 verified "10+" against a requirement of 20 |
| 7 — bounded labels | Quality flags were an unconstrained string array |
| 8 — named mechanism | Security was goals-only; no mechanism for any hop |

A checklist is used because the review demonstrated that competent judgement missed all nine.
