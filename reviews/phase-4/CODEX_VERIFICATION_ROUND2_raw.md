# Codex independent verification - Phase 4, round 2

> Raw output, verbatim, `codex exec -s read-only`, 2026-10-02, against commit `bea70f0`. Verdict: **ACCEPT**.

Round-2 answers:

P4-COD-001: CLOSED. `POST /api/v1/demo/faults` is now mapped in `local`, protected by operator auth, and returns contract-valid `401`, `403`, and `429`. `production-like` leaves it unmapped, matching AC-031 and OpenAPI. `demo` startup refusal is acceptable for Phase 4 because only demo-only `202`/`400` behavior is deferred to Phase 11.

P4-COD-002: OPEN. The new test no longer has the original “dropped rows can pass” hole, but the replacement assertion is internally inconsistent: it expects `existing.Count + 1` rows while also asserting `seen` is a subset of `existing`. With the current keyset query, the older between-page insert should fall after the cursor and appear, so a correct implementation can fail this test.

New findings:

P4-COD-003 - P3 - `docs/11-service-design/OPERATIONS_API.md:176` - Security prose says demo endpoints are absent outside the demo profile - this contradicts AC-031 and the same file’s config table at line 152, where `local` returns `403` and only `production-like` is unmapped - fix by narrowing the sentence to “absent from production-like; disabled with 403 outside demo where mapped.”

P4-COD-004 - P3 - `docs/06-development/TEST_SPECIFICATIONS.md:521` - Automated-status prose still says `POST /demo/faults` is excluded and asserted absent - this is stale after the round-2 fix and conflicts with lines 513-518 - fix the automated note to say `401`/`403`/`429` are exercised in local and only `202`/`400` remain excluded.

Verdict line requested:

ACCEPT (P0=0 and P1=0)
