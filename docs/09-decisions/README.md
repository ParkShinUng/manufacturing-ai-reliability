# Decision Records

A **Decision Record (DEC)** captures *how a decision was challenged* — the adversarial exchange
between Claude Code (primary engineer) and Codex (independent challenger), and the evidence that
resolved it.

An **ADR** (`docs/07-adr/`) captures *what was decided*. The two are complementary: the ADR is the
durable architectural statement; the DEC is the audit trail showing the decision survived challenge.

## Protocol

| Round | Actor | Action |
|---|---|---|
| 1 | Claude | proposal with rationale and alternatives |
| 1 | Codex | adversarial challenge: simpler alternative, hidden coupling, new SPOF, races, restart holes, testability |
| 2 | Claude | classify each finding: `ACCEPTED` / `PARTIALLY_ACCEPTED` / `REJECTED_WITH_EVIDENCE` / `HUMAN_DECISION_REQUIRED` |
| 2 | Codex | re-evaluate |
| 3 | both | compare alternatives explicitly; converge or escalate |

Maximum 3 rounds. Unresolved disagreement on architecture boundary, control authority, safety,
security, data integrity, persistence semantics, failure recovery, or core infrastructure escalates
to the human — **neither agent may decide unilaterally**.

## Status values
`PROPOSED` · `CONVERGED` · `HUMAN_DECISION_REQUIRED` · `SUPERSEDED`

## v0.3 register

| ID | Title | Status | Human review | ADR |
|---|---|---|---|---|
| [DEC-001](DEC-001-fallback-authority.md) | Fallback authority when the Supervisor dies | CONVERGED | **APPROVED** (HD-003) | ADR-0011 |
| [DEC-002](DEC-002-replay-eligibility.md) | Safety Supervisor replay eligibility | CONVERGED | not required | ADR-0012 |
| [DEC-003](DEC-003-command-ordering.md) | Command ordering and supersession | CONVERGED | not required | ADR-0013 |
| [DEC-004](DEC-004-manual-mode.md) | Manual/operator command origin | CONVERGED (removed) | **APPROVED** (HD-002) | ADR-0014 |
| [DEC-005](DEC-005-ood-semantics.md) | OOD hard reject vs reduced authority | CONVERGED | not required | ADR-0015 |
| [DEC-006](DEC-006-command-authenticity.md) | Command-origin authenticity | CONVERGED | not required | ADR-0016 |
| [DEC-007](DEC-007-contract-precedence.md) | Contract precedence and event envelope | CONVERGED | not required | ADR-0017 |
| [DEC-008](DEC-008-sensor-representation.md) | Missing/failed sensor representation | CONVERGED | not required | ADR-0018 |
| [DEC-009](DEC-009-model-authorization.md) | Model authorization during registry outage | CONVERGED | **APPROVED** (HD-001) | ADR-0019 |

## Outcome summary

Codex **rejected 3 of 9** Round-1 proposals (DEC-001, DEC-003, DEC-004) and required modification of
five more. All three rejections were upheld by Claude. In two cases Codex's alternative was adopted
outright over Claude's proposal (DEC-003, DEC-004). In Round 2 Codex identified two further design
defects (DEC-003 timestamp ordering, DEC-009 publisher silence) that produced materially better
designs in Round 3.

Claude recorded **no** `REJECTED_WITH_EVIDENCE` classifications against Codex. One partial divergence
was retained with evidence (`producer` in the event envelope, DEC-007).
