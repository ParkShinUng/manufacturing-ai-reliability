# Codex Findings — v0.3

> Independent adversarial review results across three rounds.
> Raw transcripts: [`reviews/v0.3/`](../../../reviews/v0.3/).

## 1. Why this document matters

Codex was not a rubber stamp. Across three rounds it **rejected 3 of 9** architecture proposals,
required modification of 5 more, escalated 1 to the human, and twice found that a "fixed" design had
simply moved the problem. In two cases its alternative was adopted over Claude's proposal outright.

**11 of the 62 reconciled gap findings were found by Codex alone**, four of them material to Phase 1.

## 2. Round 1 — independent gap analysis

30 findings. The four Claude missed entirely, and why each mattered:

| ID | Finding | Why it mattered |
|---|---|---|
| CODEX-GAP-019 | **No simulator physics** — no sensor ranges, units, degradation equations, dynamics, or stop conditions | Phase 1's primary deliverable would have been invented in code |
| CODEX-GAP-020 | **No OPC UA node map, no Modbus register map** — no address space, data types, scaling, endianness, or write addresses | Phase 1 and 2 protocol work was literally unbuildable |
| CODEX-GAP-010 | **`explicit human/manual mode`** permitted as a production command origin with nothing defining it | A documented bypass of the entire deterministic safety path, sitting in a contract document |
| CODEX-GAP-015 | `MASTER_SPEC` "any failed gate rejects" vs `FAILURE_MODEL` "OOD may reject **or reduce AI authority**" | Two different safety semantics on the same path |

Also Codex-only: fallback configuration contract (GAP-029), numeric retry/timeout profiles
(GAP-053), gate threshold values (GAP-030), equipment-state source contract (GAP-031), offset commit
policy (GAP-050), PostgreSQL read-model schemas (GAP-080), contract precedence ambiguity (GAP-010).

## 3. Round 1 — challenge of the 9 decisions

| Decision | Codex verdict | Outcome |
|---|---|---|
| DEC-001 fallback authority | **REJECT** | Upheld. Epoch fencing added. |
| DEC-002 replay eligibility | ACCEPT_WITH_MODIFICATION | Both modifications adopted. |
| DEC-003 command ordering | **REJECT** | Upheld. **Codex's alternative adopted.** |
| DEC-004 manual mode | **REJECT** | Upheld. **Claude's proposal withdrawn.** |
| DEC-005 OOD semantics | ACCEPT_WITH_MODIFICATION | Adopted. |
| DEC-006 command authenticity | ACCEPT_WITH_MODIFICATION | All three modifications adopted. |
| DEC-007 contract precedence | ACCEPT_WITH_MODIFICATION | Adopted, one partial divergence retained with evidence. |
| DEC-008 sensor representation | ACCEPT_WITH_MODIFICATION | All four modifications adopted. |
| DEC-009 model authorization | **NEEDS_HUMAN_DECISION** | Redesigned; residual bound → HD-001. |

### The single most valuable finding of the review

> *"Supervisor sends AI command at T0, network delays it; Control Service silence timeout fires at
> T+15 s and writes 60 %; delayed command arrives at T+16 s with unexpired/accepted payload unless
> explicitly fenced."* — Codex, DEC-001

Claude's watchdog would have been defeated by exactly the condition it existed to handle. Expiry
alone could not fix it, because the command was legitimately unexpired. This produced the
`controlEpoch` fencing token, which also closes split-brain and the two-replica case.

## 4. Round 2 — re-evaluation

Codex returned **NOT_CONVERGED** with 9 open decisions and 12 new defects. Two categories:

**(a) Genuine new design defects** — Claude had moved a problem rather than solving it:

| ID | Finding |
|---|---|
| DEC-003 | *"`predictedAtUtc` is produced by the prediction host wall clock… Kafka partitioning orders records after production; it does not make producer timestamps monotonic."* A consumer-group rebalance can briefly give two instances the same partition. **The replacement design was also wrong**, and was replaced again. |
| DEC-009 | *"If quarantine occurs while `mlops-publisher` is down, Kafka remains reachable and quiet, so the Supervisor never trips the proposed staleness bound."* **Silence is indistinguishable from "nothing changed."** This produced the liveness watermark. |

**(b) Claimed-but-not-executed edits.** Claude had written its Round-2 response before making the
file changes, so Codex correctly found that `control.proto` still carried `source`, `MASTER_SPEC`
still had 9 gates, `API_AND_COMMAND_CONTRACTS.md` still allowed manual mode, and
`telemetry.schema.json` still had string flags and non-nullable measurements. **Codex was right to
call this out**, and all were executed in Round 3.

**12 concrete defects (CODEX-R2-001…012)**, all fixed, including two P0 addressing bugs Claude
introduced: an inconsistent topic name, and a Modbus holding register with **no per-equipment base
address**, which would have aliased every machine's setpoint to the same address.

## 5. Round 3 — final verification

Codex verified all 21 Round-2 items (DEC-001…009 plus CODEX-R2-001…012) and returned
**NOT_IMPLEMENTATION_READY** with 2 P0 and 5 P1 new findings. **Five were real defects Claude had
introduced**, including two outright bugs:

| ID | Sev | Finding | Disposition |
|---|---|---|---|
| CODEX-R3-001 | P0 | **The supersession predicate was inverted.** `sourcePredictionAtUtc > lastAccepted -> COMMAND_SUPERSEDED` would have rejected every *newer* command — the exact opposite of the intent. Also, DEC-003 claimed timestamps were "not used for ordering" while the contracts still used them. | **ACCEPTED.** Predicate fixed to `<=`. The check is retained but reclassified honestly as a *secondary* guard whose failure mode is asymmetric — a clock step can only make it reject a valid command, never accept a stale one. DEC-003 corrected to say so. |
| CODEX-R3-005 | P1 | **Modbus register collision.** `sourceEpochMs` at +20 is 32-bit and occupies +20 **and** +21, but `statusBitmap` had been placed at +21 — aliasing it onto the timestamp's high word. | **ACCEPTED.** Moved to +22; block read extended to 23 registers. |
| CODEX-R3-003 | P1 | The prose command example still carried the self-asserted `source` field while the rules section said it was removed. | **ACCEPTED.** Examples rewritten to match `control.proto`. |
| CODEX-R3-004 | P1 | **Watermark records had no contracted key on a compacted topic** — they would compact unpredictably or displace a model's authorization. | **ACCEPTED.** Sentinel key `__watermark__`, enforced by schema conditionals. |
| CODEX-R3-006 | P1 | The safety-decision schema did not enforce exactly 13 gates in canonical order. | **ACCEPTED.** `minItems`/`maxItems`/`prefixItems` pin the order; verified by a negative test. |
| CODEX-R3-007 | P1 | SC-01…SC-10 were prose only; an unsafe configuration would pass validation. | **ACCEPTED.** `tests/contract/safety_invariants.mjs` checks each invariant against the reference config **and** a deliberately mutated config, so a vacuous check is itself a failure. |
| CODEX-R3-002 | P0 | "Claims the gate is closed but is blocked on human approval and unresolved HD decisions." | **Pushed back with evidence.** `HUMAN_APPROVAL.md` exists. More importantly the state Codex flags is the *intended* one: v0.3 is an Implementation Ready **Candidate** *awaiting* approval. Candidate is not approved, and the gate being closed pending a human is the requested design, not a defect. |

Raw transcripts: [`reviews/v0.3/CODEX_FINAL_VERIFICATION.md`](../../../reviews/v0.3/CODEX_FINAL_VERIFICATION.md)
and [`CODEX_ROUND3_REVERIFICATION.md`](../../../reviews/v0.3/CODEX_ROUND3_REVERIFICATION.md).

### What this round demonstrates

The final verification was not a formality. Codex found an **inverted predicate** and a **register
collision** that would both have produced real defects in Phase 1 and Phase 7 code — and it found
them in work Claude had already declared complete and self-consistent. Two dependency-free
verification scripts had passed against that same work; they checked what they were written to check
and could not see either bug. That is the argument for adversarial review over self-checking, stated
concretely rather than as a principle.

## 6. Disposition

| Status | Count |
|---|---|
| **RESOLVED** in v0.3 | 58 of 62 reconciled findings |
| **Deferred with a record** | 4 (graded AI authority, manual control, multi-broker HA, toolchain re-verification) |
| **Escalated to human** | 3 (HD-001, HD-002, HD-003) |
| **Unresolved P0** | 0 |
| **Unresolved P1** | 0 |

## 7. Where Claude did not follow Codex

Exactly one, recorded with evidence rather than quietly overridden:

**DEC-007 — `producer` in the event envelope.** Codex argued for a minimal envelope. Claude retained
`producer` because the `causationId` chain cannot be audited across services without knowing which
service emitted each link, and NFR-002 requires that traceability. Codex's substantive objection —
that a universal `sequence` field imports unresolved ordering semantics into every event — **was**
accepted, and `sequence` was removed from the envelope.

No finding was classified `REJECTED_WITH_EVIDENCE`.

## 8. Honest assessment of the process

The dual-agent protocol cost three review rounds and produced materially better architecture:

- the **most severe** finding (Supervisor death strands equipment) came from Claude and Codex missed it;
- the **most dangerous latent** finding (the manual-mode bypass) came from Codex and Claude missed it;
- the **two most subtle** findings (wall-clock ordering, publisher silence) came from Codex
  challenging designs Claude had already declared fixed.

Neither agent alone would have produced this specification. That is the strongest available evidence
that the protocol earned its cost — and it is worth noting that the process only worked because Codex
was instructed to disagree and Claude was required to classify rather than defend.
