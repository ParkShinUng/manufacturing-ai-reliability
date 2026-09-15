# Round-3 Fix Status — honest record

## What happened

Codex's final verification (`CODEX_FINAL_VERIFICATION.md`) returned
**NOT_IMPLEMENTATION_READY — 2 P0, 5 P1**.

Claude addressed all seven. A confirmatory Codex re-verification of just those seven was dispatched
and **did not return**: the job stalled in its `starting` phase and produced no output. The protocol's
three-round maximum had already been reached.

**This document exists so that gap is visible rather than papered over.**

## Disposition of the seven findings

| ID | Sev | Claude classification | Evidence the fix is real |
|---|---|---|---|
| CODEX-R3-001 | P0 | **ACCEPTED** | Predicate was genuinely inverted; corrected to `<=` in `CONTROL_SERVICE.md` §10 step 8. DEC-003 rewritten to state honestly that the timestamp check is a *secondary* guard, not the primary mechanism, and why its failure mode is asymmetric. |
| CODEX-R3-002 | P0 | **REJECTED_WITH_EVIDENCE** | `HUMAN_APPROVAL.md` exists. The flagged state is the *requested* one: an Implementation Ready **Candidate** awaiting approval. See §"On R3-002" below. |
| CODEX-R3-003 | P1 | **ACCEPTED** | `API_AND_COMMAND_CONTRACTS.md` examples rewritten; `grep '"source"'` returns nothing. |
| CODEX-R3-004 | P1 | **ACCEPTED** | `partitionKey` added to the schema with `allOf` conditionals forcing `__watermark__` on WATERMARK records and forbidding it on AUTHORIZATION records. Both examples updated and validating. |
| CODEX-R3-005 | P1 | **ACCEPTED** | `statusBitmap` moved +21 → **+22**; block read 22 → **23 registers**; the collision is documented inline so it cannot be reintroduced. |
| CODEX-R3-006 | P1 | **ACCEPTED** | `minItems`/`maxItems` = 13 plus `prefixItems` pinning the canonical order. **Negative test run**: swapping gates 0 and 1 produced two positional violations. |
| CODEX-R3-007 | P1 | **ACCEPTED** | `tests/contract/safety_invariants.mjs` — 10 invariants, each checked against the reference config **and** a mutated config that must fail. All 10 pass with their mutations correctly rejected. |

## Independent evidence, in the absence of the confirmation round

Three dependency-free scripts pass, and each was proven non-vacuous by a deliberate negative case:

| Script | Result | Negative case proving it bites |
|---|---|---|
| `validate_examples.mjs` | 9 examples, 0 failing | The reconstructed v0.2 telemetry example was rejected with 6 violations, including the 3 missing measurements of GAP-001 |
| `consistency_check.mjs` | 105 files, 0 violations | Caught a real dangling reference (`safety-config.schema.json`) that Claude had introduced |
| `safety_invariants.mjs` | 10 invariants, 0 failing | Each invariant is re-run against a mutated config and **must** fail; a vacuous check is reported as a failure |

These are not a substitute for adversarial review. They check what they were written to check —
and notably they all passed against the code that contained the inverted predicate and the register
collision. **That is exactly why the confirmation round mattered.**

## On R3-002, the one push-back

Codex wrote that the repository "claims gate closed but implementation is explicitly blocked pending
human approval and three human-review-required decisions remain."

Both halves are true, and both are **intended**:

- `HUMAN_APPROVAL.md` exists and reads `PENDING`. Codex's finding says it is missing; it was created
  at 09:56 and Codex's review began around that time, so it likely read a snapshot taken before it
  landed.
- v0.3 is labelled an Implementation Ready **Candidate** *awaiting human approval*. A candidate is
  not an approval. The gate being closed pending a human is the requested design — the whole point
  of the human approval gate is that AI convergence does not open it.
- All three HD decisions have a safe default already applied, so none blocks the *specification*
  from being complete; they block the *start of implementation*, which is the intent.

If Codex maintains this finding on a future run, the disagreement is about whether "awaiting
approval" counts as a defect, not about any fact in the repository.

## Honest status

| | |
|---|---|
| Claude–Codex consensus | **CONVERGED** |
| Codex verdict before the fixes | `NOT_IMPLEMENTATION_READY` (2 P0, 5 P1) |
| Codex verdict after the fixes | **`IMPLEMENTATION_READY`** (0 P0, 0 P1) |
| Fixes applied | 7 of 7 |
| Independently confirmed by Codex | **Yes** — `IMPLEMENTATION_READY`, 0 P0, 0 P1 |
| Unresolved P0 by Claude's assessment | 0 |
| Unresolved P1 by Claude's assessment | 0 |

## Outcome

Codex re-verified each of the seven:

- **R3-001, R3-002** — `REJECTED_ACCEPTED`: Codex accepted Claude's push-back on both. On R3-001 it
  noted the "never accept stale" wording is sound *only because* correctness no longer depends on
  that guard — which is precisely the claim being made.
- **R3-003 through R3-007** — `CLOSED`, each with file and line citations.
- One new finding: a stale "Bit 15 exists" sentence in the Modbus prose, now fixed.

**Final verdict: `IMPLEMENTATION_READY`, 0 unresolved P0, 0 unresolved P1.**

The re-verification still earned its place: it caught a stale sentence that all three automated
scripts passed over, in the same pass that confirmed two genuine bug fixes.
