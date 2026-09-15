# Manufacturing AI Reliability Platform v0.3 — Human Review Summary

> **This is the document to read.** Target reading time 5–10 minutes.
> You do not need to read every ADR or contract; links are provided where you want depth.

---

## 1. Status

| Item | Value |
|---|---|
| Specification | **v0.3 — Implementation Ready Candidate** |
| Engineering review | **Claude Code + Codex, 3 rounds, complete** |
| Human decision | **APPROVED_WITH_CONDITIONS** — all conditions satisfied (2026-09-15) |
| Implementation | **Phase 1 IN PROGRESS** — requested and started 2026-09-15 |
| Project state | **`V0.3_APPROVED_PHASE_1_IN_PROGRESS`** |
| Claude–Codex consensus | **CONVERGED** — Codex final verdict `IMPLEMENTATION_READY`, 0 P0, 0 P1 |

**No application code was written for this review.** The packet below describes the repository as it
stood at approval: specifications, decisions, contracts, diagrams, and test specifications only.
Application code first appeared on 2026-09-15, after the gate opened and Phase 1 was requested.
Three dependency-free verification scripts exist under
`tests/contract/` and all three pass.

## 2. What changed from v0.2, in one paragraph

v0.2 was a well-organised architecture baseline with a serious gap: it described a platform whose
headline claim — *"AI must never become a single point of failure"* — was **not actually guaranteed
by the architecture**. v0.3 closes that, plus 61 other findings, and adds the implementation-ready
detail (simulator physics, OT address maps, service designs, full contracts, failure matrix, test
specifications) that v0.2 did not have.

## 3. The dual-agent review actually worked

This matters because it is the main process claim of the exercise.

| | Claude | Codex |
|---|---|---|
| Independent gap findings | 59 | 30 |
| Found by **both** | 19 | |
| Found by **only one** | 31 (Claude) | **11 (Codex)** |

Codex **rejected 3 of Claude's 9** architecture proposals in Round 1, and was right all three times.
In two cases Codex's alternative was adopted outright. In Round 2 Codex found two further design
defects that produced materially better designs.

**Four things Codex found that Claude missed entirely**, all material:
1. **No OPC UA node map or Modbus register map** — Phase 1/2 protocol work was unbuildable.
2. **No simulator physics** — sensor ranges, fault equations, and dynamics were undefined, so Phase
   1's primary deliverable would have been invented in code.
3. **A documented safety bypass**: the command contract allowed *"explicit human/manual mode"* to
   originate production commands, with no state, authorization, or interlock defined anywhere.
4. **A contradiction on the safety path**: `MASTER_SPEC` said any failed gate rejects, while
   `FAILURE_MODEL` said OOD may *"reject or reduce AI authority."*

**The single most severe finding was Claude's and Codex missed it** (see §4). Neither agent alone was
sufficient, which is the strongest available evidence that the protocol earned its cost.

In the **final** verification round Codex found two more real bugs in work Claude had already
declared complete: an **inverted supersession predicate** (it would have rejected every newer
command) and a **Modbus register collision** (a status register aliased onto the high word of a
32-bit timestamp). Both would have become defects in Phase 1 and Phase 7 code. Three automated
verification scripts had passed against that same work — they checked what they were written to
check, and could not see either bug.

## 4. The most important thing on this page

> **v0.2's central reliability claim was false as written.**

"Control survives AI failure" held when you killed the *inference service* — the Safety Supervisor
stayed up, rejected the AI, and commanded fallback. It did **not** hold when you killed the
**Safety Supervisor itself**: nothing then commanded anything, and the equipment would sit
indefinitely at whatever rate the AI had last talked it into.

The fix (ADR-0011) is a three-layer authority model:

| Layer | Owner | Survives |
|---|---|---|
| **L1** advisory | Safety Supervisor | — |
| **L2** application control | **Control Service** — sole application writer, runs a 12 s fallback watchdog | Supervisor death |
| **L3** equipment self-protection | Simulated equipment — protective trips + 30 s dead-man | **total platform death** |

Codex then found a race in that fix: a command issued *before* the watchdog fired but delayed in the
network would arrive *after* the fallback, still unexpired, and undo it. That is closed by a
**control epoch fencing token**, the same pattern used in distributed lock systems.

This is now testable: `FAIL-SUP-001`, `FAIL-SUP-002`, `FAIL-PLAT-001` (AC-011, AC-013, AC-012).

## 5. Findings and their disposition

| Severity | Found | Resolved in v0.3 | Deferred with a record | Open |
|---|---|---|---|---|
| BLOCKER | 22 | 22 | 0 | 0 |
| HIGH | 27 | 27 | 0 | 0 |
| MEDIUM | 10 | 8 | 2 | 0 |
| LOW | 3 | 1 | 2 | 0 |
| **Total** | **62** | **58** | **4** | **0** |

Full backlog: [`reviews/v0.3/RECONCILED_GAP_ANALYSIS.md`](../../../reviews/v0.3/RECONCILED_GAP_ANALYSIS.md).

## 6. Major architecture decisions

Nine decisions went through the full challenge protocol. Details:
[`docs/09-decisions/`](../../09-decisions/README.md).

| ID | Decision | Outcome | Your review needed? |
|---|---|---|---|
| DEC-001 | Fallback when the Supervisor dies | 3-layer authority + epoch fencing | **YES** |
| DEC-002 | Supervisor replay eligibility | not replay-eligible; `seekToEnd` on restart | no |
| DEC-003 | Command ordering | **no distributed counter**: 2 s command TTL < 5 s cadence makes concurrency structurally impossible | no |
| DEC-004 | Manual/operator commands | **removed from the baseline** | **YES** (scope) |
| DEC-005 | OOD semantics | hard reject; one canonical gate table | no |
| DEC-006 | Command authenticity | mTLS; `source` removed from the contract | no |
| DEC-007 | Contract precedence | machine-readable schemas outrank prose | no |
| DEC-008 | Dead sensor representation | nullable values + closed quality enum | no |
| DEC-009 | Model authorization | published to Kafka, never polled from MLflow | **YES** (risk bound) |

## 7. Why each major technology is here

Every one had to answer "what requirement makes this necessary?"

| Technology | Requirement it serves | Would removing it break something? |
|---|---|---|
| **Kafka** | multiple independent consumers, replay demos, lag as a signal | yes — but it is deliberately **not** on the command path |
| **gRPC** | bounded synchronous command path independent of Kafka | yes — ADR-0009 |
| **C#/.NET** | OT protocols, deterministic control, long-running reliability | — |
| **Python** | ML ecosystem | — |
| **PostgreSQL** | read models + **authoritative Control Service state** | yes |
| **MLflow** | model lineage, promotion, rollback | yes |
| **`mlops-publisher`** *(new)* | removes MLflow from the safety path | yes — see DEC-009 |
| **Kubernetes** | failure isolation, rollout, recovery demos | deferred until local is stable (ADR-0005) |
| **Next.js** | operator UI | no control impact by design |

**Deliberately excluded**: Lakehouse/Spark/Airflow (ADR-0007), 3D twin (ADR-0004), LLM copilot,
multi-broker HA, and — new in v0.3 — **manual operator control** (ADR-0014).

## 8. Control and safety architecture

```
AI prediction (advisory, no credentials)
   → 13 deterministic safety gates, ALL must pass
      → bounded command intent
         → Control Service: authenticate, fence, expire, bound, dedupe
            → equipment (one writable setpoint, nothing else)
```

Properties now guaranteed rather than asserted:
- AI **cannot** bypass the gates — it holds no equipment credentials and no write path exists.
- Command origin is **authenticated**, not self-declared.
- A **replayed** prediction cannot move equipment (three independent mechanisms).
- A **reordered** command cannot apply (structurally impossible + epoch fencing).
- A **quarantined** model loses authority in ~2 s, and a dead publisher is detected in 90 s.
- A **dead sensor** is `null` with a flag — never a fabricated number fed to the gates.

## 9. Major failure behaviour

32 failure modes, each classified **CONTROL-CRITICAL** or **CONTROL-INDEPENDENT**
([`FAILURE_MODEL.md`](../../02-architecture/FAILURE_MODEL.md)). The classification makes the
isolation claim falsifiable rather than rhetorical.

Control-independent (must never affect control): Kafka, PostgreSQL, MLflow, dashboard, Operations
API, observability.
Control-critical: equipment protocols, Control Service, Safety Supervisor, `mlops-publisher`, clock
skew, network partition.

## 10. Remaining risks

Six open risks, none blocking, all with mitigations:
[`OPEN_RISKS.md`](OPEN_RISKS.md). The two worth your attention are **R-01** (all performance figures
are unmeasured targets) and **R-03** (the platform is deliberately single-instance, so it demonstrates
*recovery*, not *high availability* — and must never be presented as the latter).

## 11. What we need from you

**Three decisions** — [`HUMAN_DECISIONS_REQUIRED.md`](HUMAN_DECISIONS_REQUIRED.md):

| ID | Question | Safe default already applied |
|---|---|---|
| **HD-001** | How long may a possibly-quarantined model keep control authority if Kafka is unreachable? Proposed **60 s**. | yes, 60 s |
| **HD-002** | Should operators ever be able to command equipment directly? Currently **no**. | yes, removed |
| **HD-003** | Approve the control-authority change in ADR-0011 (Control Service may write autonomously on watchdog). | yes, implemented |

All three have a safe default in place, so none blocks progress — but HD-003 changes the control
authority model and should not be ratified silently.

## 12. Recommendation

**APPROVE_WITH_CONDITIONS**, conditions being the three decisions above, then begin
**Phase 1 — Equipment Simulator / HIL**.

Phase 1 is genuinely unblocked: the simulator now has physics, sensor ranges, fault equations, a
state machine with a transition table, protective conditions, and OT address maps precise enough to
implement against — none of which existed in v0.2.

Checklist: [`V0.3_APPROVAL_CHECKLIST.md`](V0.3_APPROVAL_CHECKLIST.md).
Record your decision in [`HUMAN_APPROVAL.md`](HUMAN_APPROVAL.md).

**Until that file says `APPROVED` or `APPROVED_WITH_CONDITIONS`, implementation must not start.**

---

## 13. Final verification outcome

Codex's full verification returned **NOT_IMPLEMENTATION_READY** (2 P0, 5 P1). **Five were real
defects Claude had introduced**, including an **inverted supersession predicate** and a **Modbus
register collision**. All seven were addressed and re-verified.

**Codex's re-verification verdict: `IMPLEMENTATION_READY`, 0 unresolved P0, 0 unresolved P1.**
Six findings closed outright; on the seventh Codex accepted Claude's push-back that "awaiting human
approval" is the intended state, not a specification defect. It found one further stale sentence in
the Modbus prose during that pass, which is also now fixed.

Worth noting for your confidence in the artefact: the three automated verification scripts passed
against the code that contained the inverted predicate and the register collision. They check what
they were written to check. The adversarial round is what caught those two, which is the concrete
argument for the protocol over self-checking.

Transcripts: [`reviews/v0.3/CODEX_FINAL_VERIFICATION.md`](../../../reviews/v0.3/CODEX_FINAL_VERIFICATION.md),
[`CODEX_ROUND3_REVERIFICATION.md`](../../../reviews/v0.3/CODEX_ROUND3_REVERIFICATION.md),
[`ROUND3_FIX_STATUS.md`](../../../reviews/v0.3/ROUND3_FIX_STATUS.md).
