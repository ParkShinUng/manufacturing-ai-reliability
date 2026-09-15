# Human Decisions — v0.3 (RESOLVED 2026-09-15)

> **All four decisions are recorded.** HD-001, HD-002 and HD-003 were **approved as proposed**, and
> HD-004 (overall v0.3 approval) is **APPROVED_WITH_CONDITIONS**, and the conditions — the three
> decisions themselves — are **all satisfied**. Phase 1 is authorized but not started.
> Authoritative record: [`HUMAN_APPROVAL.md`](HUMAN_APPROVAL.md).

| ID | Decision | Outcome |
|---|---|---|
| HD-001 | Max authorization staleness | **APPROVED** — 60 s |
| HD-002 | Manual/operator command origin | **APPROVED** — keep removed |
| HD-003 | ADR-0011 control authority | **APPROVED** — ratified |
| **HD-004** | **Overall v0.3 approval** | **APPROVED_WITH_CONDITIONS** — all conditions satisfied |

The detail below is retained as the record of what was asked.

---


> This document contains **only** decisions that genuinely need a human. Minor engineering choices
> were resolved between Claude and Codex and are recorded in `docs/09-decisions/`.
> **Three decisions. Each already has a safe default applied, so none blocks progress** — but none
> should be ratified silently either.

---

## HD-001 — Maximum window a possibly-quarantined model may retain control authority

**Type:** risk tolerance
**Raised by:** Codex (escalated in Round 1, mechanism redesigned in Round 3)
**Decision record:** [DEC-009](../../09-decisions/DEC-009-model-authorization.md) · ADR-0019

### The question
Model authorization (including quarantine) is published to a compacted Kafka topic and consumed by
the Safety Supervisor, which **never calls MLflow on the decision path**. Quarantine normally
propagates in about 2 seconds, and a dead publisher is detected within 90 seconds.

But if the Supervisor cannot reach **Kafka at all**, it cannot learn of a quarantine. How long may it
continue to honour its last-known authorization before treating all models as unauthorized?

### Options
| Option | Consequence |
|---|---|
| **60 s (proposed, applied)** | Up to 60 s during which a quarantined model could still influence a setpoint — bounded by the gates, the 10 pp per-decision limit, and the 25 pp/60 s budget. |
| 0 s (fail closed immediately) | Any Kafka blip drops every machine to fallback. Noisy, and Kafka is classified CONTROL-INDEPENDENT precisely so it cannot do this. |
| 300 s (the original proposal) | Codex objected: five minutes is a long time for a known-bad model to retain authority. |

### Why this is yours and not ours
It is a risk-tolerance judgement, not an engineering default. Codex said so directly: *"That may be
acceptable for a portfolio simulator, but it is a human risk-tolerance decision."*

### Recommendation
**60 s.** Bounded, and the rate-change limits cap how far a bad model could move anything inside that
window. Reconsider if this platform ever moves toward real equipment.

**Decision: APPROVED as proposed (2026-09-15).**
---

## HD-002 — Should operators ever command equipment directly?

**Type:** product scope
**Raised by:** Codex (found the latent bypass; proposed removal)
**Decision record:** [DEC-004](../../09-decisions/DEC-004-manual-mode.md) · ADR-0014

### The question
v0.2 permitted *"explicit human/manual mode"* to originate a production command — with no state, no
authorization, no interlock, and no relationship to the Safety Supervisor. It was a documented bypass
of the entire safety path.

Claude proposed specifying it properly as a fifth control mode. **Codex rejected that** as spec bloat
that would add a fifth mode while the existing four were still undefined, and warned that a
documented-but-unimplemented bypass reads as approved architecture. Claude withdrew the proposal and
**removed it instead**.

### Current state
The Safety Supervisor is the only production command origin. Manual control is recorded as explicitly
Deferred with **no contract surface**. The only operator-gated action is releasing `STOP_REQUIRED`.

### Options
| Option | Consequence |
|---|---|
| **Keep removed (applied)** | Strongest safety story; a real plant would eventually want manual control. |
| Reintroduce at P1 | Needs its own ADR, state machine, authorization model, interlocks, and AC set. |
| Reintroduce now | Rejected by both agents — it would delay Phase 1 for a feature nothing currently requires. |

### Recommendation
**Keep removed for the baseline.** For a platform whose thesis is *bounded* AI authority, having
exactly one authenticated command origin is a stronger statement than having two.

**Decision: APPROVED as proposed (2026-09-15).**
---

## HD-003 — Ratify the control authority change (ADR-0011)

**Type:** control authority / architecture boundary
**Raised by:** Codex (flagged for escalation: *"It changes the safety/control authority model and may
violate the 'sole equipment-write owner' principle in FR-035/ADR-0002"*)
**Decision record:** [DEC-001](../../09-decisions/DEC-001-fallback-authority.md) · ADR-0011

### The question
To make *"control survives AI failure"* true for Supervisor death as well as inference death, the
**Control Service may now write to equipment autonomously** — driving to the configured fallback rate
after 12 s of Supervisor silence — and the **simulated equipment** holds an independent 30 s dead-man
revert.

Codex asked whether this weakens FR-035 ("no other application component may write directly to
equipment").

### Our answer, for you to accept or reject
It does not, and FR-035 has been clarified rather than weakened:
- the Control Service was **already** the sole application writer; it now writes on its own initiative
  as well as on instruction, which does not add a writer;
- the equipment dead-man is the **equipment protecting itself**, in the same class as the
  over-temperature trip it already has. FR-035 constrains *application* components.

### What you are ratifying
1. Control Service may write **without** a Supervisor command, only to the configured fallback rate.
2. Simulated equipment may revert its own setpoint after 30 s without a refresh.
3. FR-035's wording is scoped to application components.

### Why this needs you
It is the one change in v0.3 that alters **who may move equipment**. That should not pass on two AI
agents' agreement.

### Recommendation
**Ratify.** Without it, the platform's headline claim is false as written — and that is the first
thing a competent reviewer will test.

**Decision: APPROVED as proposed (2026-09-15).**
---

## Not requiring a human decision

For completeness, these were resolved between the agents and are recorded rather than escalated:
command ordering (DEC-003), OOD semantics (DEC-005), command authenticity (DEC-006), contract
precedence (DEC-007), sensor representation (DEC-008), replay eligibility (DEC-002), and all 58
resolved gap findings.
