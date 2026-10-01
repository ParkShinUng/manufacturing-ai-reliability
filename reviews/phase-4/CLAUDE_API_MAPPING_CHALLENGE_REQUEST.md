# Operations API read-model mapping — challenge request to Codex

> Read-only challenge, Phase 4 step 4, before any route is written. `OPERATIONS_API.md` §10a
> (proposed 2026-10-01) says which stored value each response field is; the OpenAPI contract named
> the fields but not their sources. Three more fields are made nullable under `OD-012`'s rule:
> `EquipmentSummary.qualityOverall`, `/models` `watermarkAgeSeconds`, `PlatformHealth.fallbackRatePct`
> (uncommitted — see `git diff`). Contract change and schema semantics: mandatory participation.

## What to challenge

1. Each mapping row: is it the field's meaning, or a convenient stand-in? In particular
   `activeReasonCodes` (latest decision's reason codes — is "active" right for an `ACCEPT`?),
   `commandedRatePct` (the outcome's `requestedTargetPct`), `controlMode` (latest outcome's
   `resultingMode`, though Control Service owns the mode), `updatedAtUtc`.
2. **Equipment existence**: current view for list/detail, any history for decisions/commands. Sound
   for AC-029 and OD-021?
3. **The three new nullables**: a correct application of OD-012's rule, or a widening it does not
   permit? Is `fallbackRatePct` from PostgreSQL compatible with "health answers without PostgreSQL"
   (the ADR-0024 C02 resolution)?
4. **Keyset paging** with `(occurred_at_utc, id)`, `from` inclusive / `to` exclusive, opaque cursor:
   stable under concurrent inserts as AUDIT-001 requires? Partitioned tables and the key order?
5. **Trace**: one statement, "latest" decision and outcome per correlation ID — can there be more
   than one, and does choosing the latest hide anything AC-047 must show?
6. **Health**: `kafkaReachable` defined as "every projector consumer caught up in the last 30 s" —
   honest, or a different thing under that name?
7. Anything that conflicts with OD-011–OD-021, ADR-0024, AC-030's harness, or the test
   specifications API-001, TRACE-001, AUDIT-001.

## Output format

Per finding: `P4-M-nnn · severity · file:line · defect · why it matters · fix`. End with `ACCEPT`,
`ACCEPT_WITH_CONDITIONS` (list), or `REVISE`. Do not modify any file.
