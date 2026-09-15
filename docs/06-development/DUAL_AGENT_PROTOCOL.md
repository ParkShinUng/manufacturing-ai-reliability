# Dual-Agent Engineering Protocol

> The permanent workflow for all future changes. v0.2's `DEVELOPMENT_PROTOCOL.md` described a
> documentation-first sequence but had no independent-review step; this document adds it and is
> referenced from there.

## 1. Roles

| Actor | Role | May do | May **not** do |
|---|---|---|---|
| **Claude Code** | Primary engineer, primary architect, orchestrator, specification owner | propose, decide routine matters, implement, self-test, reconcile docs | decide alone on authority/safety/security/data-integrity; mark its own work verified |
| **Codex** | Independent challenger, second opinion, verification agent | challenge, propose alternatives, verify **read-only**, recommend fixes | implement the fixes it recommends |
| **Human** | Product owner, final architecture approver | approve, reject, set scope, resolve escalations | — |

**Formal verification is read-only.** Codex may recommend a fix; Claude implements it. This preserves
implementer/reviewer independence — a reviewer who edits the code is reviewing its own work on the
next pass.

## 2. When Codex participation is mandatory

Any change affecting: service boundaries · adding or removing a service · control authority ·
equipment safety · AI safety · fallback logic · distributed messaging · Kafka semantics ·
synchronous communication · retries · timeouts · idempotency · concurrency · persistence ·
data ownership · protocol choice · model deployment · rollback · security boundaries · OT trust
boundaries · schema versioning · major infrastructure · major dependency · failure recovery ·
Kubernetes architecture.

For anything else, Claude decides and records the decision. Escalating trivia dilutes the signal.

## 3. Decision protocol

| Round | Actor | Output |
|---|---|---|
| 1 | Claude | proposal: problem, evidence, decision, **alternatives considered**, open values marked `TBD-WITH-DECISION` |
| 1 | Codex | challenge: simpler alternative · hidden coupling · new SPOF · concurrency · restart/recovery hole · operational burden · testability · contract problems · **does it actually close the gap, or only appear to** |
| 2 | Claude | classify **every** finding: `ACCEPTED` / `PARTIALLY_ACCEPTED` / `REJECTED_WITH_EVIDENCE` / `HUMAN_DECISION_REQUIRED` |
| 2 | Codex | re-evaluate the revised design |
| 3 | both | compare alternatives explicitly against requirements, reliability, safety, complexity, maintainability, testability, operations, failure impact, portfolio relevance |

**Maximum 3 rounds.** Remaining disagreement on architecture boundary, control authority, safety,
security, data integrity, consistency, persistence semantics, failure recovery, or core
infrastructure → `HUMAN_DECISION_REQUIRED`. Neither agent may decide unilaterally.

`REJECTED_WITH_EVIDENCE` requires stated reasoning. "Considered and declined" is a valid outcome;
"ignored" is not.

## 4. The 20-step workflow for every future feature

1. Change Request
2. Requirements — name the FR/NFR/AC IDs
3. **Claude proposal**
4. **Codex challenge**
5. Evidence-based convergence
6. Decision Record (`docs/09-decisions/`)
7. ADR if architecture-significant
8. Architecture update
9. Contract update — **before** producers or consumers
10. Acceptance criteria
11. Test specification
12. Implementation plan, stating what will **not** change
13. **Claude implementation**
14. Claude self-test
15. Documentation reconciliation
16. **Codex independent verification** (read-only)
17. Claude resolution of findings
18. **Codex re-verification**
19. Human review if required
20. Merge

Steps 4, 16, and 18 are the ones v0.2 lacked. In the v0.3 review, step 4 rejected 3 of 9 proposals,
step 16 found two genuine bugs in work already declared complete, and step 18 confirmed the fixes.

## 5. Codex during implementation

| Mode | Use |
|---|---|
| **ASK** | second opinion on a bounded question |
| **CHALLENGE** | adversarial review of an approach before committing to it |
| **VERIFY** | independent read-only verification of finished work |

**Call Codex proactively, not only at the end**, when working on: concurrency · Kafka offsets ·
retry logic · state machines · the Safety Supervisor · protocol reconnection · durable buffering ·
schema evolution · model promotion · control commands · data integrity · failure recovery.

Waiting until the end on a high-risk area means the challenge arrives after the design has hardened.

## 6. Definition of Done for a dual-agent change

Additional to `DEFINITION_OF_DONE.md`:

- [ ] Codex verification run and its output preserved in `reviews/<version>/`
- [ ] **P0 = 0, P1 = 0** in the final verification
- [ ] Every finding classified; none silently dropped
- [ ] Decision Record reconciled with what was actually built
- [ ] Human approval where required

## 7. Honesty rules

These exist because each was violated at least once during the v0.3 review.

1. **Do not claim an edit you have not made.** Codex caught Claude claiming fixes that were still
   pending, because the response was written before the files were changed.
2. **Do not let automated checks stand in for adversarial review.** Three verification scripts passed
   against code containing an inverted predicate and a register collision; both were found by Codex.
   Scripts check what their author suspected.
3. **Record disagreement rather than resolving it silently.** Raw transcripts are kept in
   `reviews/<version>/` precisely because a summary written by one party to a disagreement is not
   evidence.
4. **AI consensus is not authorization.** Two agents agreeing that something is implementation-ready
   is exactly the claim the human is being asked to approve.
