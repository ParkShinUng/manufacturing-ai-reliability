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
Migrate → start projectors → serve once projection lag < threshold (readiness gate, so the dashboard
never shows a half-built read model as current).

## 10. Normal flow
`GET /api/v1/equipment` → summaries; `GET /api/v1/equipment/{id}` → detail incl. mode, model version,
active reason codes; `GET /api/v1/equipment/{id}/decisions?from&to&page` → paged decisions;
`GET /api/v1/trace/{correlationId}` → telemetry→prediction→decision→command chain (**AC-006**);
`GET /api/v1/platform/health` → inference availability, lag, error rate, fallback rate;
`POST /api/v1/demo/faults` → 403 unless `PROFILE=demo`.

## 11. Failure behaviour
| Failure | Behaviour |
|---|---|
| PostgreSQL down | 503 + problem details; **no control impact** |
| Kafka down | projections pause; API serves stale data **with an explicit staleness field** |
| Projection lag high | `X-Data-Staleness-Seconds` header so the UI can show it rather than silently lying |

## 12. Timeout / retry / idempotency / ordering
Request timeout 5 s; DB timeout 3 s; reads idempotent; pagination is keyset-based (stable under
concurrent inserts, unlike offset pagination).

## 13. Backpressure
Rate limiting per client; max page size 500; projections batch-commit.

## 14. Restart recovery
Stateless; projectors resume from offsets.

## 15. Configuration
`PROFILE` (local|demo|production-like), DB/Kafka connections, page limits, auth settings.

## 16. Security
Session/JWT with role claims: `viewer` (read), `operator` (read + `STOP_REQUIRED` reset + demo
faults). Demo endpoints **absent** outside the demo profile, not merely hidden. **The dashboard never
holds equipment-write credentials, and no Operations API route reaches the Control Service.**

## 17. Observability
`http_requests_total{route,status}`, `http_request_duration_seconds`, `projection_lag_seconds`,
`demo_fault_injections_total`.

## 18. Performance targets (TARGET — unmeasured)
P95 summary ≤ 100 ms; P95 trace ≤ 200 ms; 50 concurrent dashboard clients.

## 19. Test strategy
Contract tests against OpenAPI (request and response); authz matrix tests per role; demo-endpoint
403 outside demo profile; AC-006 trace; staleness header correctness.

## 20. Acceptance criteria
AC-006, AC-009, AC-030, AC-031.
