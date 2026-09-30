# Codex challenge of ADR-0024 - round 1

> Raw output, transcribed verbatim from the `codex exec -s read-only` run of 2026-09-30 (prompt
> pointed at `CLAUDE_ADR-0024_CHALLENGE_REQUEST.md`). Verdict: **REVISE** (3 P1, 3 P2, 1 P3).

ADR-0024-C01 • severity P1 • Contract testing for AC-030 • B3 proves only wrapped component-schema validation, not full OpenAPI response validation • AC-030 says every Operations API response validates against the OpenAPI contract, including error responses; that includes route/status selection, content type, headers, and problem responses, not just JSON body shape • Either choose an OpenAPI-aware validator or explicitly specify and spike a harness that uses YAML + Corvus to validate status code, content type, required headers, selected response schema, `$ref`, `nullable`/`anyOf`, `format`, and error responses.

ADR-0024-C02 • severity P1 • Hidden contract conflict • The OpenAPI file lacks `503` on several DB-backed routes and lacks `X-Data-Staleness-Seconds` on `/platform/health` even though `OPERATIONS_API.md` says PostgreSQL down returns 503 and projection-backed responses carry the staleness header • A correctly implemented service could fail AC-030 because its documented failure/staleness responses are not valid OpenAPI responses • Reconcile `contracts/openapi/operations-api-v1.yaml` with `OPERATIONS_API.md`: add the missing 503 responses or narrow the service doc, and decide whether `/platform/health` is projection-backed.

ADR-0024-C03 • severity P1 • Bearer-token validation • The JwtBearer decision names ES256, issuer, audience, exp, skew, and roles, but does not pin several trust-boundary behaviors: required `sub`, max token lifetime, `nbf`, `kid` lookup/unknown `kid`, duplicate key IDs, rotation overlap, `MapInboundClaims`, required signed tokens, and malformed roles • OD-018 rate limiting depends on `sub`, and default/misconfigured JWT behavior is exactly where auth bugs hide • Specify the exact validation policy and add B2 negatives for missing `sub`, excessive lifetime, future `nbf`, unknown/duplicate `kid`, missing/malformed roles, and claim mapping disabled.

ADR-0024-C04 • severity P2 • Local token minting • `scripts/mint-token.mjs` is specified before its key path, key format, `kid` convention, file permissions, and ignore rule are pinned; `.gitignore` covers broad `*.pem`/`*.key`, but the ADR does not name the generated directory or prove the private key cannot be committed • Local/demo auth is still a credential boundary, and an underspecified script can silently mint tokens the API accepts for the wrong reason • Name the key directory, public/private filenames, `kid` derivation, signature encoding test against JwtBearer, and explicit git-ignore coverage.

ADR-0024-C05 • severity P2 • Migrations • The in-repository runner says every migration runs in its own transaction, but does not state how non-transactional PostgreSQL DDL is handled, especially `CREATE INDEX CONCURRENTLY`; it also leaves advisory-lock scope/keying and history-table bootstrap details to implementation • A migration runner is persistence infrastructure, and hidden edge cases here can create exactly the half-migrated or concurrent-start behavior the ADR is trying to avoid • Either forbid non-transactional DDL for Phase 4 with an explicit offline-indexing rule, or add a migration-file flag/model for non-transactional migrations and spike it; specify advisory-lock key derivation and bootstrap behavior.

ADR-0024-C06 • severity P2 • Forward-only migrations • The ADR says forward-only replaces `OPERATIONAL_DATA.md` §19’s “Migration up/down”, but the referenced document still says “Migration up/down” • The decision is reasonable for audit-bearing tables, but as written the spec and ADR disagree on the test contract • Amend §19 before acceptance to the forward-only tests named in ADR-0024: up from empty, second run no-op, failure leaves schema unchanged, edited applied file refused, concurrent runners apply once.

ADR-0024-C07 • severity P3 • Licence gate • ADR-0024 correctly notes `PostgreSQL` must be added to `scripts/license-scan.mjs`, and the current allow-list does not include it • This is not a design blocker if B1 is enforced, but acceptance should not imply the existing scanner already passes Npgsql • Keep B1 as a hard Phase 4 spike condition and update the allow-list before adding the package.

Verdict: REVISE.

Plain Npgsql over Dapper/EF Core is sound for OD-014/OD-015, Testcontainers core is consistent with ADR-0021, and forward-only migrations are acceptable in principle. The blockers are the incomplete AC-030 proof and the underspecified JWT trust boundary.

---

# Round 2 - 2026-09-30

> Raw output, verbatim. Verdict: **REVISE** (C01-C07 CLOSED, one new P1).

Round 2 challenge result. I did not modify any files.

**Round 1 Items**
- ADR-0024-C01: CLOSED. ADR now pins an AC-030 harness for status, content type, headers, body schema, asserted `uuid`/`date-time` formats, and documented-status coverage.
- ADR-0024-C02: CLOSED. OpenAPI now adds `503` to database-backed routes, adds the missing error responses, and explicitly defines `/platform/health` as not database/projection-backed with no staleness header and no `503`.
- ADR-0024-C03: CLOSED. JWT policy now pins ES256-only signed tokens, exact issuer/audience, required `sub`, `kid` behavior, duplicate `kid` startup failure, `MapInboundClaims = false`, role handling, and B2 negatives.
- ADR-0024-C04: CLOSED. Local token key directory, filenames, JWK format, `kid` thumbprint, max lifetime, public/private separation, and `.local/` ignore coverage are specified.
- ADR-0024-C05: CLOSED. Migration runner now pins advisory-lock keying, bootstrap, checksum refusals, transaction behavior, and explicitly excludes non-transactional DDL from migrations.
- ADR-0024-C06: CLOSED. `docs/11-service-design/OPERATIONAL_DATA.md` §19 is amended to forward-only migration tests.
- ADR-0024-C07: CLOSED. Licence scanner update remains correctly captured as a hard B1 Phase 4 spike condition before adding Npgsql.

**New Finding**
ADR-0024-C08 - P1 - Bearer-token validation / Validation policy - `iat` is required and `exp - iat <= 3600 s`, but the policy never requires `iat <= now + clockSkew`. - Why it matters: a token with `iat` far in the future, no `nbf`, and `exp = iat + 3600` can satisfy the stated lifetime rule while being accepted long before its intended issuance time, potentially bypassing the max-token-lifetime boundary. - Fix: Pin `iat` validation: reject tokens whose `iat` is later than `now + 60 s`; add this negative to B2.

Verdict: REVISE.

---

# Round 3 - 2026-09-30

> Raw output, verbatim. Verdict: **ACCEPT**.

ADR-0024-C08 is CLOSED.

No new P0/P1 findings.

ACCEPT
