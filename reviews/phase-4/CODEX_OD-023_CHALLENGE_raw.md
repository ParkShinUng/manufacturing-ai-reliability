# Codex challenge of OD-023

> Raw output, verbatim, `codex exec -s read-only`, 2026-10-02. Verdict: **ACCEPT_WITH_CONDITIONS**, option A.

Verdict: SOUND_WITH_CONDITIONS

Recommended option: A, but Claude’s recommendation needs tightening before approval. The direction is right: do not weaken `SECURITY_BOUNDARIES.md`; make audit tables insert-only; keep `control_outcome` long enough for mode transitions; enforce roles. But option A currently overclaims determinism and least privilege unless the conditions below are written into OD-023 and the affected specs/tests.

1. Define audit duplicate semantics precisely: `safety_decision` and `control_outcome` are first-wins by Kafka offset order for the duplicate identity. A later duplicate at a different offset must not update the row. Tests must include same-identity duplicates at later offsets, including a conflicting payload case, and assert the chosen behavior is observable rather than silently mistaken for a normal rewrite.

2. Amend OD-014 / PROJ-001 wording: range rebuilds do not “replace” audit rows. For insert-only audit tables, replay is a no-op for already-present rows and only inserts missing retained rows. AC-046 is still proven by canonical equality plus fresh-store replay, but not by claiming existing audit rows were replaced.

3. Keep audit repair separate from replay. Once an audit row exists, replay must not correct it by UPDATE/DELETE. If corruption/missing-history recovery is required, it must be backup/restore or a documented operator procedure, not the projector rebuild path.

4. Retention must be documented as `control_outcome` 1 year whole. The current “90 days outcomes, 1 year mode transitions” claim in [SECURITY_BOUNDARIES.md:52](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/docs/02-architecture/SECURITY_BOUNDARIES.md:52) cannot remain as an exact per-row delete rule while OD-019 stores mode transitions in `control_outcome`.

5. Role creation must respect OD-016. Phase 4 may create/grant only the `operations` schema roles. The Phase 7 `control` schema and grants belong to control-service migrations, not Operations migrations.

6. The retention job needs its own least-privilege design. Do not give the runtime projector/API connection the migration owner role. Prefer a dedicated maintenance role or narrowly-scoped security-definer function that can drop only eligible `operations` partitions by allowlisted table and upper-bound check, with no row `DELETE` or audit `UPDATE`.

7. Tests must be updated: `AUDIT-001` currently still expects `90 d decisions and outcomes, 1 y mode history` at [TEST_SPECIFICATIONS.md:452](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/docs/06-development/TEST_SPECIFICATIONS.md:452). It must expect `control_outcome` 1 year whole, `safety_decision` 90 days, `equipment_state_history` 1 year, and prove no audit UPDATE/DELETE privileges exist on runtime roles.

Findings:

P4-OD23-001 — Severity P1 — [OPEN_DECISIONS.md:1276](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/docs/08-roadmap/OPEN_DECISIONS.md:1276)  
Defect: Option A says first-wins is deterministic, but does not define behavior for duplicate identities at different offsets with non-identical payloads.  
Fix: Specify first-wins by offset order and require duplicate/conflict tests and observability.

P4-OD23-002 — Severity P1 — [TEST_SPECIFICATIONS.md:167](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/docs/06-development/TEST_SPECIFICATIONS.md:167)  
Defect: PROJ-001 says rows in the replay range are replaced. That is false for insert-only audit tables under option A.  
Fix: Split rebuild expectations: rebuildable tables are replaced; audit tables are unchanged or missing rows inserted; canonical dump remains equal.

P4-OD23-003 — Severity P1 — [OPEN_DECISIONS.md:1276](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/docs/08-roadmap/OPEN_DECISIONS.md:1276)  
Defect: “DDL by the migration owner’s role” does not define a safe runtime retention connection. If the service holds the migration owner connection, least privilege is weakened.  
Fix: Add a dedicated retention privilege model with no row DELETE/UPDATE and no broad runtime owner role.

P4-OD23-004 — Severity P1 — [OPEN_DECISIONS.md:1276](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/docs/08-roadmap/OPEN_DECISIONS.md:1276)  
Defect: “Phase 4 creates the roles of §16” overreaches OD-016 for the Phase 7 `control` schema.  
Fix: Phase 4 creates only operations-side roles/grants; Phase 7 creates control schema grants.

P4-OD23-005 — Severity P2 — [TEST_SPECIFICATIONS.md:452](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/docs/06-development/TEST_SPECIFICATIONS.md:452)  
Defect: AUDIT-001 still requires 90-day retention for outcomes, which conflicts with option A’s 1-year whole-table `control_outcome` retention.  
Fix: Update the test to match the decided longer retention.

ACCEPT_WITH_CONDITIONS: conditions 1-7 above.
