# ADR-0024 — operational store access, migrations, API hosting, authentication and contract testing
Status: **Accepted** (2026-10-01, by the product owner). Proposed 2026-09-30, **revised after round 1** of the Codex challenge
([`reviews/phase-4/CODEX_ADR-0024_CHALLENGE_raw.md`](../../reviews/phase-4/CODEX_ADR-0024_CHALLENGE_raw.md),
`REVISE`, then round 2 `REVISE`, round 3 **`ACCEPT`**) — see the classification sections at the end. Group B conditions are Phase 4 entry conditions.
`persistence`, `major dependency`, `security boundaries` and `synchronous communication` are on the
mandatory participation list (`DUAL_AGENT_PROTOCOL.md` §2).
Phase: Phase 4 entry condition (`P4-DOR-009`; `OD-017` condition 2).

## Context

Phase 4 builds the operations projector and the Operations API (`OPERATIONAL_DATA.md`,
`OPERATIONS_API.md`). `TOOLCHAIN.md` fixes PostgreSQL **major 18** and nothing else. Five choices are
open, and the decisions of 2026-09-30 constrain every one of them:

- **`OD-014`** — rebuild equality is a canonical dump; projection tables may hold **no**
  non-deterministic column (no sequence, no `now()` default, no insertion time), and every row
  carries its provenance offset. Whatever writes the tables must write exactly what it is told.
- **`OD-015`** — `telemetry_reading_1s` is an order-independent conditional upsert:
  replace only when `(eventTimeUtc, sequence)` is greater.
- **`OD-016`** — one migration history per owner schema; Phase 4 migrates `operations` only.
- **`OD-017`** — ES256 bearer tokens, the API holds public keys only, a repository script mints
  local tokens, and nothing skips validation.
- **`OD-018`** — rate limit per token subject, readiness after caught-up, a staleness header.
- **`AC-030`** — every response validates against the OpenAPI 3.1 contract, error responses included.

## Decision drivers

1. **Nothing between the code and the SQL** where `OD-014` and `OD-015` need exact statements.
2. **No dependency for what a few lines do** — this repository's standing rule, and the reason
   ADR-0020 was amended to an in-repository Modbus reader.
3. **Security code comes from the platform, not from this repository.** Hand-written JWT
   validation is the one place "a few lines" is the wrong answer.
4. One JSON Schema validator in the repository, already chosen (`ADR-0022`).
5. Permissive licences only, enforced by `scripts/license-scan.mjs` (`ADR-0023`).

## Considered options

### Database access

| | Option | Assessment |
|---|---|---|
| **A** | **`Npgsql` 10.0.3, plain ADO.NET** | the de facto PostgreSQL driver; one dependency (`Microsoft.Extensions.Logging.Abstractions`); every statement is written out, which is what `OD-014`/`OD-015` require |
| B | `Npgsql` + `Dapper` 2.1.89 | saves the mapping loop; the projector writes, and the API reads a handful of shapes, so the saving is small |
| C | EF Core 10.0.12 + `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 | a model, change tracking and generated SQL between the code and the upsert; key generation and defaults are exactly what `OD-014` forbids, and they would have to be switched off one by one |

### Migrations

| | Option | Assessment |
|---|---|---|
| **A** | **In-repository runner over versioned SQL files** — `NNNN_name.sql` per schema, applied in order, each in its own transaction, recorded with a SHA-256 checksum in `<schema>.schema_migrations`, serialised by a PostgreSQL advisory lock; a changed checksum of an applied file stops the service. Details pinned under *Migration runner* below | about a hundred lines; PostgreSQL DDL is transactional, so a failed migration leaves the schema unchanged, which is `OPERATIONAL_DATA.md` §11's "does not start against a half-migrated schema" |
| B | `dbup-postgresql` 7.0.1 | the same model as A, maintained elsewhere; one more dependency pinned to its own Npgsql range |
| C | `FluentMigrator` 8.0.1 | up/down migrations in C#; a DSL between the schema and SQL, and a larger dependency |
| D | EF Core migrations | only with option C above |

**Forward-only, not up/down.** `OPERATIONAL_DATA.md` §19 names "migration up/down" as a test. A down
migration for a table that holds audit history is a delete that looks like a rollback, and the only
tables where it would be harmless are the rebuildable ones. The test becomes: up from empty; a
second run is a no-op; a failing migration leaves the schema unchanged; an edited applied file is
refused. §19 is amended with this ADR.

#### Migration runner — pinned (round 1, `C05`)

- **Lock.** A session-level `pg_advisory_lock(1296124242, n)` — `1296124242` is `MAIR` in ASCII, `n`
  is the schema's fixed number (`operations` = 1, `control` = 2). Taken before anything else, held
  for the whole run, released at the end. Two runners serialise; the second finds nothing to apply.
- **Bootstrap.** Under the lock, in the first transaction: `CREATE SCHEMA IF NOT EXISTS`, then the
  history table `(version, name, sha256, applied_at_utc)`. `applied_at_utc` is operational metadata
  on a non-projection table; `OD-014`'s determinism rule covers projection tables only.
- **One transaction per file.** A statement that cannot run in a transaction — `CREATE INDEX
  CONCURRENTLY`, `VACUUM`, `ALTER SYSTEM` — **fails the migration** by PostgreSQL's own rule, so the
  schema is left unchanged and startup stops. Non-transactional DDL is therefore not supported, by
  design: on a table small enough for Phase 4, an ordinary `CREATE INDEX` in the transaction is
  correct, and an online index build is an operator runbook step, not a migration.
- **Refusals.** A gap in version numbers, a file whose checksum differs from the recorded one, or a
  recorded version with no file: startup stops with the reason. Nothing is repaired automatically.

### API hosting

**ASP.NET Core minimal APIs** from the shared framework (`Microsoft.AspNetCore.App`, in the SDK — no
package). Its built-in rate limiter (`System.Threading.RateLimiting` token bucket, partitioned by the
`sub` claim) covers `OD-018`'s 20 requests/s, burst 40. There is no serious alternative on .NET, and
none is considered.

### Bearer-token validation

| | Option | Assessment |
|---|---|---|
| **A** | **`Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.12** (brings `Microsoft.IdentityModel.*` 8.19.2) | the platform's validator; configured with **static** keys — no metadata endpoint, no discovery, no network call. The full policy is pinned under *Validation policy* below |
| B | Hand-written validation on `System.Security.Cryptography.ECDsa` | no dependency, and every classic JWT defect — `alg: none`, algorithm confusion, missing `exp` — becomes this repository's to get right |

#### Validation policy — pinned (round 1, `C03`)

| Rule | Value |
|---|---|
| signature | required (`RequireSignedTokens`); `ValidAlgorithms = [ES256]` only — `none`, `HS*`, `RS*` refused |
| key selection | `kid` **required** and must match exactly **one** configured key; an unknown `kid` is refused; a key set with a duplicate `kid` **fails startup**. The key set may hold several keys, which is how rotation overlaps |
| `iss`, `aud` | exact match: `mair-local-issuer` locally, the environment's issuer otherwise; `mair-operations-api` |
| `exp`, `iat` | both required; lifetime `exp − iat` ≤ **3600 s**, and `iat` no later than now + 60 s — else refused. Without the second rule a token issued "in the future" would pass the lifetime check and be usable long before and after its hour (round 2, `C08`) |
| `nbf` | honoured when present |
| clock skew | **60 s**, for `exp` and `nbf` |
| `sub` | required, non-empty — rate limits partition on it (`OD-018`); absent → `401` |
| `roles` | a JSON array of strings; `viewer` and `operator` are recognised, anything else ignored. A valid token with no recognised role → `403` |
| claim mapping | `MapInboundClaims = false`: claim names reach the code as issued, so `sub` and `roles` are not renamed |

#### Local tokens — pinned (round 1, `C04`)

`scripts/mint-token.mjs`, Node's built-in `crypto`, no package. On first use it generates a P-256
key pair into **`.local/keys/`** — git-ignored by name in `.gitignore`, not only by the `*.key`
pattern — as `operations-api-es256.private.jwk` (mode `0600` where the platform supports it) and
`operations-api-es256.public.jwk`. `kid` is the key's **RFC 7638 thumbprint**. Tokens are ES256 with
IEEE-P1363 (`r‖s`) signatures, lifetime **15 min** by default and never above 3600 s, with `sub`,
`roles`, `iss`, `aud`, `iat`, `exp` and `kid` set. The API is given the public JWK only; no build,
image or compose file references the private one.

### Contract testing for AC-030

The contract is YAML; the validator (`ADR-0022`) takes JSON Schema text. OpenAPI 3.1 schemas **are**
JSON Schema 2020-12, so no OpenAPI-specific validator is needed — only a YAML reader and a wrapper
that points at the response's schema from inside the document, so its `$ref`s resolve:
`{ "$ref": "#/components/schemas/EquipmentSummary", "components": { … } }`.

| | Option | Assessment |
|---|---|---|
| **A** | **`YamlDotNet` 18.1.0, test projects only** | MIT, no dependencies; YAML → JSON, then `Corvus.Text.Json.Validator`. Only the body is JSON Schema — the rest of a response is checked by the harness below |
| B | `Microsoft.OpenApi` + `.YamlReader` 3.10.2 | a full OpenAPI object model, plus `SharpYaml`; the model is not what is being tested |
| C | Keep a JSON copy of the contract | two sources for one contract — `ADR-0017` forbids it |

#### The AC-030 harness — pinned (round 1, `C01`)

A body schema is one of five things a response must match. For every response a test receives, the
harness looks the request up in the contract and fails on any of:

1. **status** — the `(path template, method, status)` triple is not documented. An undocumented
   status is a contract failure, not a pass-through; `default` responses are not used.
2. **content type** — differs from the documented media type (`application/json` for success,
   `application/problem+json` for errors).
3. **headers** — a documented header is absent, or its value fails the header's schema.
4. **body** — fails the selected response schema, validated by Corvus through the wrapper, with
   `$ref`, `allOf`, `anyOf` and `null` resolved as JSON Schema 2020-12 resolves them.
5. **format** — `uuid` and `date-time` are **asserted**, not annotation-only. Corvus's handling is
   checked in B3; if it treats `format` as annotation, the harness asserts those two itself.

And one check over the whole run: every documented `(path, method, status)` is exercised at least
once by the suite, so an error response nobody triggers cannot hide.

### PostgreSQL for tests and local runs

`postgres` **18.6**, digest `sha256:5a5a84b19854a9ffaa54082c166ff4ec27473a361e496e5ea167f298f2da9722`
(published 2026-09-24), started by **`Testcontainers` 4.15.0 core** — already pinned by ADR-0021 — as
the Kafka broker is, not by the `Testcontainers.PostgreSql` module: one more package for a container
the core API starts in a few lines.

## Decision

| Concern | Choice | Where |
|---|---|---|
| database access | `Npgsql` **10.0.3**, plain ADO.NET | operations-service |
| migrations | in-repository forward-only runner, per-schema history, checksum, advisory lock | operations-service |
| API hosting, rate limiting | ASP.NET Core minimal APIs, built-in rate limiter | shared framework — no package |
| token validation | `Microsoft.AspNetCore.Authentication.JwtBearer` **10.0.12**, static ES256 keys | operations-service |
| token minting | `scripts/mint-token.mjs`, Node `crypto` | local and demo only |
| contract tests | `YamlDotNet` **18.1.0** + `Corvus.Text.Json.Validator` 5.6.1, inside the harness above | test projects only |
| test and local database | `postgres:18.6@sha256:5a5a84b1…a9722` via `Testcontainers` 4.15.0 core | tests, compose |

## Consequences

- **The licence gate must learn `PostgreSQL`.** Npgsql declares the SPDX licence `PostgreSQL` — an
  OSI-approved permissive licence — which `scripts/license-scan.mjs`'s allow-list does not contain.
  It is added to the list; no evidence override is needed.
- **The canonical dump is SQL, not `COPY`'s defaults.** `COPY … TO` text output depends on
  `DateStyle`, `TimeZone` and `extra_float_digits`; the dump either fixes those session settings or
  formats every column explicitly. Deciding which is a spike item, not left to the implementation.
- **Forward-only migrations** amend `OPERATIONAL_DATA.md` §19 — done with this revision (`C06`).
- **The contract gained the responses a correct service gives** (`C02`, and a gap found while
  checking it): `400` on every route with parameters, `403` on every route (a valid token without
  the role), `404` on `/equipment/{id}/commands`, `503` on every database-backed route.
  `/platform/health` is stated to be neither projection- nor database-backed: it answers when
  PostgreSQL does not, with unavailable dependencies as field values.
- **A new trust boundary in code**: `JwtBearer` configured wrongly accepts tokens it should not. The
  negative tests are part of the decision (B2 below), not of the implementation.

## Acceptance conditions

### Group A — verified before proposing

| | Condition | Result |
|---|---|---|
| A1 | Versions are current stable on nuget.org as of 2026-09-30 | **yes** — Npgsql 10.0.3, JwtBearer 10.0.12, YamlDotNet 18.1.0 |
| A2 | Licences from the nuspecs | Npgsql `PostgreSQL`; JwtBearer `MIT`; YamlDotNet `MIT` |
| A3 | Dependencies | Npgsql → `Microsoft.Extensions.Logging.Abstractions` only; JwtBearer → `Microsoft.IdentityModel.Protocols.OpenIdConnect` 8.19.2; YamlDotNet none |
| A4 | Image pinned by digest | **done** |

### Group B — Phase 4 spike conditions

If any is false, Phase 4 stops and this ADR is amended rather than worked around (the ADR-0021
precedent).

- **B1 — the licence graph.** Restored graph scanned; every package permissive after `PostgreSQL`
  is added to the allow-list.
- **B2 — token validation refuses what it must.** Against the configured validator: a script-minted
  ES256 token is **accepted**, with `sub` and `roles` reaching the code unrenamed. Each of these is
  **refused**: `alg: none`; HS256 signed with the public key's bytes; RS256; a wrong `aud`; a wrong
  `iss`; expired beyond 60 s; `nbf` in the future beyond 60 s; no `exp`; no `iat`; lifetime over
  3600 s; `iat` more than 60 s in the future; no `sub`; no `kid`; an unknown `kid`; a token signed by another key. A key set with a
  duplicate `kid` fails startup. A valid token whose `roles` is missing, not an array, or holds no
  recognised role gets `403`, not `200`. No network call is made at startup or per request.
- **B3 — the AC-030 harness catches each kind of mismatch.** One deliberately wrong response per
  check — an undocumented status, a wrong content type, a missing staleness header, a body missing a
  required key, a non-null where the schema allows only its `anyOf` members, a malformed `uuid` — is
  each refused, and the correct response passes. The coverage check fails when a documented status
  is removed from the suite.
- **B4 — the database container.** `postgres:18.6` starts under Testcontainers on Windows and on the
  CI runner, with a readiness check that does not pass on the first of PostgreSQL's two "ready"
  log lines (the init server's).
- **B5 — the migration runner.** A failing migration leaves no partial schema; a second run applies
  nothing; an edited applied file stops startup; two runners started together apply each file once.
- **B6 — the canonical dump is stable.** The same rows dump to the same bytes across two sessions
  with different `DateStyle`, `TimeZone` and `extra_float_digits`.

### Group B — status, 2026-10-01

| | Result | Evidence |
|---|---|---|
| B1 | **DONE** — 61 packages, all permissive, after `PostgreSQL` was added to the allow-list. Before it, the scan failed on Npgsql, so the gate was checked as well as passed | `scripts/license-scan.mjs` |
| B2 | **DONE** — a token from `scripts/mint-token.mjs` is accepted with `sub` and `roles` unrenamed; 16 refusals each give `401`; three no-recognised-role shapes give `403`; a token asserting the internal role claim gains nothing; no authority, metadata address or configuration manager exists; a duplicate `kid` and a private key in the key set fail startup. Mutation-checked: re-enabling `TryAllIssuerSigningKeys` fails "no kid" and "unknown kid", dropping the `iat` rule fails its case | `TokenValidationTests` (23) |
| B3 | **DONE** — on the real contract: a conforming body with an allowed `null` passes, an error response is checked too, and an undocumented status, a wrong media type, a missing or malformed header, a missing required key, a value outside an `anyOf`, a malformed `uuid` and a malformed `date-time` each fail; coverage shrinks as responses are seen. Corvus asserts `format` | `OpenApiHarnessTests` (12) |
| B4 | **DONE on Windows**; CI on the next push. Readiness is a TCP connection and `SELECT 1`, which the init server cannot answer; the server reports 18.6 | `PostgresDatabase`, `MigrationRunnerTests.TheServerIsTheOnePinned` |
| B5 | **DONE** — up from empty then a no-op second run; a half-failing file leaves nothing; `CREATE INDEX CONCURRENTLY` fails the migration; an edited applied file, a version gap and a recorded version with no file are refused; two runners started together apply each file once. The edited-file test found a defect first: the checksum was an initialiser, which a `with` expression copies, so an edited migration kept its old checksum. It is computed now | `MigrationRunnerTests` (7) |
| B6 | **DONE** — identical dumps under three different `TimeZone`/`DateStyle`/`extra_float_digits` settings; the exact text form asserted; sub-millisecond timestamps and keyless tables refused rather than dumped ambiguously | `CanonicalDumpTests` (4) |

## Round 1 classification (2026-09-30)

Codex: `REVISE` — plain Npgsql, Testcontainers core and forward-only migrations judged sound; the
blockers were the AC-030 proof and the trust boundary. Every finding was checked against the
repository before it was classified.

| ID | Sev | Classification | Change |
|---|---|---|---|
| C01 | P1 | **ACCEPTED** | *The AC-030 harness*: status, content type, headers, body, `format`, plus a coverage check. Still YAML + Corvus: none of the five checks needs an OpenAPI object model, only a lookup |
| C02 | P1 | **ACCEPTED**, widened | confirmed: `503` missing on four database-backed routes. Checking it found more — **no** GET route documented `400` or `403`, and `/equipment/{id}/commands` had no `404`. All added; `/platform/health` stated to be neither projection- nor database-backed |
| C03 | P1 | **ACCEPTED** | *Validation policy* table; B2 extended with every negative it names |
| C04 | P2 | **ACCEPTED** | *Local tokens*: directory, filenames, `kid` = RFC 7638 thumbprint, lifetime, `.local/` added to `.gitignore` by name |
| C05 | P2 | **ACCEPTED** | *Migration runner*: lock key, bootstrap, non-transactional DDL fails the migration by design |
| C06 | P2 | **ACCEPTED** | `OPERATIONAL_DATA.md` §19 amended now, not at acceptance |
| C07 | P3 | **ACCEPTED** as written | the allow-list changes with the package, under B1 |

Round 2: C01–C07 **CLOSED**; one new finding.

| ID | Sev | Classification | Change |
|---|---|---|---|
| C08 | P1 | **ACCEPTED** | `iat` must not be later than now + 60 s; added to the policy table and to B2's negatives |

## Sources

- nuget.org package metadata, read 2026-09-30.
- Docker Hub `library/postgres` tag listing, read 2026-09-30.
