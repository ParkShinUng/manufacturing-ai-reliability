# Service Design — `operations-service` (Operations API)

> Runtime: ASP.NET Core. Read APIs + Kafka→PostgreSQL projections.
> Contract: `contracts/openapi/operations-api-v1.yaml` (fully specified in v0.3; GAP-005).

## 1. Purpose
Serve operator-facing read models and platform health, and host the projection workers.
**Never in the command path.**

## 2. Responsibilities
Read APIs for equipment summary, detail, decisions, commands, platform health, model deployments;
correlation-ID trace retrieval (AC-006); guarded demo fault-injection proxy; projections.

## 3. Non-responsibilities
Issues **no** equipment commands. Holds **no** equipment credentials. Performs no safety evaluation.
The dashboard talks only to this service and never to Kafka or PostgreSQL directly.

## 4. Dependencies
PostgreSQL (critical to the API, control-independent); Kafka (projections); simulator admin API
(**demo profile only**).

## 5. Inputs / outputs
In: HTTP from dashboard; Kafka events. Out: JSON per OpenAPI; PostgreSQL writes.

## 6. Contracts
`contracts/openapi/operations-api-v1.yaml` — schemas, RFC 9457 problem details, pagination, filters,
and security scheme are all specified; v0.2's `{description: OK}` stubs are gone.

## 7. Data ownership
Owns projection tables. Owns no authoritative state.

## 8. State model
Stateless API; projection workers hold only Kafka offsets.

## 9. Lifecycle
Migrate → start projectors → **ready** once every projector has been **caught up** at least once
since start (OD-018), so the dashboard never shows a half-built read model as current. Caught up:
for every partition of every topic the projector consumes, records through the high watermark
observed at a poll are written to PostgreSQL **and** their offsets committed. A lag threshold would
be meaningless on a quiet topic.

## 10. Normal flow
`GET /api/v1/equipment` → summaries; `GET /api/v1/equipment/{id}` → detail incl. mode, model version,
active reason codes; `GET /api/v1/equipment/{id}/decisions?from&to&page` → paged decisions;
`GET /api/v1/trace/{correlationId}` → prediction→decision→command chain with the prediction's
feature-window reference and its per-second readings (OD-013; **AC-047**, live chain **AC-006**);
`GET /api/v1/platform/health` → inference availability, lag, error rate, fallback rate — from the
process's own view of its dependencies, so it answers when PostgreSQL does not (no staleness
header, no 503);
`POST /api/v1/demo/faults` → 403 unless `PROFILE=demo`.

## 10a. Read-model mapping — what each response field is (Phase 4, 2026-10-01)

**Challenged by Codex** (`reviews/phase-4/CODEX_API_MAPPING_raw.md`: round 1 REVISE, round 2 ACCEPT_WITH_CONDITIONS, applied). The
OpenAPI contract names the fields; nothing said which stored value each one is, and an implementer
would have had to choose. Every field below comes from a projection table (`OPERATIONAL_DATA.md` §7)
or is `null` under `OD-012`'s one meaning — *no record observed*. "Latest" always means greatest
`occurred_at_utc`, then greatest provenance offset, so it is deterministic.

**Which equipment exist.** The list and the detail route serve the `current_equipment_state` view
and nothing else — it carries OD-021's rule, including that a state record and a tombstone are
compared by offset only because they share the equipment's key and so its partition. A
decommissioned equipment is absent from `/equipment` and `404` on `/equipment/{id}`. Its decisions
and commands stay queryable — audit outlives decommissioning (AC-029): `/decisions` and `/commands`
answer `404` only when the equipment has **no** row in `equipment_state_history`, `safety_decision`
or `control_outcome`.

| Response field | Source |
|---|---|
| `equipmentId`, `state`, `aiEligible` | the equipment's current state record |
| `controlMode` | `resulting_mode` of its latest control outcome; `null` if none |
| `qualityOverall` | `quality_overall` of its latest per-second reading; **`null` if none** (OD-012 rule, contract amended below) |
| `commandedRatePct` | `requestedTargetPct` of its latest control outcome; `null` if none |
| `appliedRatePct` | `appliedTargetPct` of its latest control outcome; `null` if none |
| `anomalyScore` | `anomaly_score` of its latest prediction; `null` if none |
| `activeReasonCodes` | FR-050's "active rejection or fallback reason": the `reasonCodes` of its latest safety decision **when that decision is `REJECT` or `FALLBACK`**; `[]` when it is `ACCEPT`, or when there is none |
| `updatedAtUtc` | the greatest `occurred_at_utc` among the records the summary was built from |
| detail `measurements` | the seven channels of its latest per-second reading, each `null` where the reading was (ADR-0018); `{}` if no reading |
| detail `qualityFlags` | that reading's flags; `[]` if no reading |
| detail `model`, `failureProbability`, `rulMinutes` | its latest prediction; absent / `null` if none |
| detail `controlEpoch` | `control_epoch` of its latest control outcome; `null` if none |
| detail `rateBudgetRemainingPp` | `null` — a Control Service quantity with no projected source |

**Decisions and commands.** `safety_decision` and `control_outcome` rows of the equipment, newest
first by `(occurred_at_utc, decision_id)` and `(occurred_at_utc, command_id)`. `from` is inclusive, `to` exclusive, both on `occurred_at_utc`.
`requestedOperationRatePct`, `boundedOperationRatePct` and `wasClamped` come from the decision's
`recommendation`, absent when it has none; `authenticatedSource`, `operatorId` and `appliedTargetPct`
from the stored outcome record.

**Paging.** Keyset, never offset: the cursor is the last row's sort key — `equipmentId` ascending
for `/equipment`; `(occurredAtUtc, decisionId)` / `(occurredAtUtc, commandId)` descending for
decisions and commands — encoded as opaque base64url JSON. The next page is the rows strictly after
the cursor in that order (`(occurred_at_utc, id) < (cursor)` for descending). **What that
guarantees:** every row that existed when the first page was served is returned exactly once, in
order, however many rows arrive between pages. A row that arrives later appears once if its key falls
after the cursor and not at all if it falls before — never twice. A cursor that does not decode, or
decodes to the wrong shape, is `400`. `limit` 1–500, default 100. `nextCursor` is `null` on the last
page.

**Trace** (`AC-047`, OD-013). One SQL statement: the prediction with `correlation_id = C`, **every**
safety decision and **every** control outcome with that correlation ID, each oldest first, and —
when there is a prediction — the per-second readings of its equipment in
`[window_start_utc, window_end_utc)`. Nothing in the contracts limits a correlation ID to one decision
or one outcome, and picking one would hide the others from an audit, so the contract's `decision` and
`command` became the arrays `decisions` and `commands` (empty = no record observed). `404` when no
prediction, decision or outcome carries `C`.

**Models.** The latest `AUTHORIZATION` per model (`partition_key`), by `authorization_sequence`.
`watermarkAgeSeconds` is now minus the watermark row's `occurred_at_utc`; **`null` when no watermark
has been observed** (OD-012 rule, contract amended below).

**Platform health** answers without PostgreSQL (§11): fields come from the process. `kafkaReachable`
is a real broker check — a metadata request with a 2 s timeout, its result cached for 5 s — not a
proxy for projector freshness, which the staleness header already reports. `maxConsumerLag` and
`predictionRecordAgeSeconds` are the projector's own measurements (`KAFKA_EVENT_BACKBONE.md` §17).
`inferenceAvailable`, `supervisorHealthy` and `controlServiceHealthy` are `null`: nothing reports
them before Phases 5–7 (OD-012). `fallbackRatePct` needs the stored control modes: it is read from
PostgreSQL and **omitted** when it cannot be measured — PostgreSQL not answering, or no current
equipment. Omitted, not `null`: `null` means *no record observed* (OD-012), and "the store is down"
is a different fact. `errorRatePct`, `authorizationWatermarkAgeSeconds` and `clockSkewSeconds` are
omitted (optional) until something measures them.

**Errors.** Every error is `application/problem+json` (RFC 9457): `400` for a malformed parameter
or cursor, `401` and `403` from token validation (ADR-0024), `404` as above, `429` from the rate
limiter, `503` when PostgreSQL does not answer within its 3 s timeout. No other status is returned.

## 11. Failure behaviour
| Failure | Behaviour |
|---|---|
| PostgreSQL down | 503 + problem details; **no control impact** |
| Kafka down | projections pause; API serves stale data and `X-Data-Staleness-Seconds` grows — the header is the one staleness mechanism, there is no second field (OD-018) |
| Projection lag high | `X-Data-Staleness-Seconds` on **every projection-backed response**: seconds since the projectors backing the route were last caught up, the **maximum** across them, 0 while caught up (OD-018) |

## 12. Timeout / retry / idempotency / ordering
Request timeout 5 s; DB timeout 3 s; reads idempotent; pagination is keyset-based (stable under
concurrent inserts, unlike offset pagination).

## 13. Backpressure
Rate limiting per token subject (`sub`): 20 requests/s, burst 40, excess answered `429` with
problem details (OD-018); max page size 500; projections batch-commit.

## 14. Restart recovery
Stateless; projectors resume from offsets.

## 15. Configuration
`PROFILE` (local|demo|production-like), DB/Kafka connections, page limits, auth settings.

**As implemented (Phase 4 step 5, 2026-10-02).** Standard .NET configuration — environment variables
use `__` for `:`. Startup fails, naming the key, if a required one is missing.

| Key | Required | Meaning |
|---|---|---|
| `Mair:Profile` | no, default `local` | `local`, `demo` or `production-like`. Phase 4 serves the same routes in every profile; the demo route arrives in Phase 11 |
| `ConnectionStrings:Operations` | **yes** | Npgsql connection string. The service sets `Command Timeout` to **3 s** whatever it says (§12) |
| `Mair:Kafka:BootstrapServers` | **yes** | the brokers the projector and the health probe use |
| `Mair:Contracts` | **yes** | the directory of the contract schemas, `contracts/jsonschema/v1` |
| `Mair:Tokens:PublicKeyFiles` | **yes** | one or more public ES256 JWK files (OD-017); a private key here fails startup |
| `Mair:Tokens:Issuer` | no, default `mair-local-issuer` | the `iss` accepted |
| `ASPNETCORE_URLS` | no | where Kestrel listens |

Page limits are **not** configuration: `limit` is 1–500, default 100, in the contract itself.

**Startup order.** Migrate the `operations` schema (OD-016) — a failure stops the process before it
listens — then start the projector, then serve. The readiness gate (OD-018) answers `503` until
every projector has caught up once. The service never creates topics: the topic bootstrap
(`AC-026`) is a separate job, and until the topics exist the projector waits and the gate stays shut.

## 16. Security
Bearer JWT (OD-017): **ES256**, validated against configured public key files — the API holds **no**
signing key. `iss` `mair-local-issuer` locally, the environment's issuer otherwise; `aud`
`mair-operations-api`; `exp` required, 60 s clock skew; roles in the `roles` claim; `sub` is the
subject rate limits count against. Local and demo tokens are minted by a repository script from a key
pair generated on the developer's machine and never committed; no build or image contains a private
key, and no route, flag or profile skips validation. Recorded as an ADR before code (`P4-DOR-009`).
Roles: `viewer` (read), `operator` (read + `STOP_REQUIRED` reset + demo
faults). Demo endpoints **absent** outside the demo profile, not merely hidden. **The dashboard never
holds equipment-write credentials, and no Operations API route reaches the Control Service.**

## 17. Observability
`http_requests_total{route,status}`, `http_request_duration_seconds`, `projection_lag_seconds`,
`demo_fault_injections_total`.

## 18. Performance targets (TARGET — unmeasured)
P95 summary ≤ 100 ms; P95 trace ≤ 200 ms; 50 concurrent dashboard clients.

## 19. Test strategy
Contract tests against OpenAPI (request and response); authz matrix tests per role; demo-endpoint
403 outside demo profile; AC-047 trace store-and-query (AC-006, the live chain, in Phase 7); staleness header correctness.

## 20. Acceptance criteria
AC-047, AC-009, AC-030, AC-031; AC-006 in Phase 7 (OD-011).
