# ADR-0024 — challenge request to Codex

> Read-only challenge of `docs/07-adr/ADR-0024-operational-store-and-operations-api-stack.md`
> (Proposed 2026-09-30), before the product owner decides. `DUAL_AGENT_PROTOCOL.md` §3 round 1.

## What to challenge

1. **Each choice against its alternatives.** Is plain Npgsql right, or does Dapper or EF Core earn
   its place? Is an in-repository migration runner a false economy against DbUp — concurrency,
   checksum, transactional DDL edge cases (e.g. `CREATE INDEX CONCURRENTLY` cannot run in a
   transaction)? Is `YamlDotNet` + Corvus actually sufficient for AC-030, or does response
   validation need things only an OpenAPI library knows (status codes, headers, content types,
   `nullable` semantics, `format`)?
2. **Forward-only migrations** replacing `OPERATIONAL_DATA.md` §19's "up/down". Sound, or a loss?
3. **The trust boundary.** Is the JwtBearer configuration described complete? What else must be
   pinned — `kid` handling, key rotation, `nbf`, token lifetime limits, `sub` absence and the rate
   limiter, `MapInboundClaims`? Is Node `crypto` minting sound (P1363 signature encoding, key
   storage, the git-ignore)?
4. **Hidden coupling or conflicts** with ADR-0017 (contracts authoritative), ADR-0021 (Testcontainers
   core, not modules), ADR-0022 (validator), ADR-0023 (CI, licence gate), OD-014 to OD-018.
5. **Group B.** Are the spike conditions the right ones and falsifiable? Anything that should be
   verified before acceptance rather than during the spike?
6. Anything else.

## Output format

`ADR-0024-Cnn · severity P0/P1/P2/P3 · section · defect · why it matters · fix`, then a verdict:
`ACCEPT`, `ACCEPT_WITH_CONDITIONS` (list them), or `REVISE` (what). Do not modify any file. The
product owner decides.
