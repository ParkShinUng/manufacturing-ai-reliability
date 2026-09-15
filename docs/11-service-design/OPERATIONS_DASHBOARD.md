# Service Design — `operations-dashboard`

> Runtime: Next.js / TypeScript. Read-only operator UI.

## 1. Purpose
Show equipment health, AI status, control mode, and platform health from **live** APIs (FR-050,
FR-051, AC-009) — never from static mocks.

## 2. Responsibilities
Equipment list and detail; anomaly/prediction display with model version; **control mode and active
rejection/fallback reason**; platform health (inference availability, Kafka lag, error rate, fallback
rate); correlation trace view; demo-only fault-injection controls, visibly marked.

## 3. Non-responsibilities
**Holds no equipment-write credentials.** Cannot command equipment. Does not access Kafka or
PostgreSQL directly — Operations API only. Contains no safety logic.

## 4. Dependencies
Operations API only. **Control-independent** (F14).

## 5. Inputs / outputs
In: Operations API JSON. Out: rendered UI; demo fault-injection POSTs (demo profile only).

## 6. Contracts
`contracts/openapi/operations-api-v1.yaml` — TypeScript types are **generated** from it, never
hand-written (NFR-007).

## 7. Data ownership
Owns none. Pure projection of API state.

## 8. State model
Client-side cache with explicit staleness display driven by the `X-Data-Staleness-Seconds` header.

## 9. Lifecycle
Static build; runtime config for API base URL and profile.

## 10. Normal flow
Poll equipment summary every 2 s; detail every 1 s while open; health every 5 s.
The UI shows **why** AI was rejected (reason codes), not merely that it was — a dashboard that shows
`fallback: true` without a reason is not operationally useful.

## 11. Failure behaviour
| Failure | Behaviour |
|---|---|
| API unavailable | explicit error state; **never** shows stale data as current |
| Stale projection | staleness banner from the response header |
| Partial failure | per-panel error, not a whole-page failure |

## 12. Timeout / retry / idempotency / ordering
Fetch timeout 5 s, 2 retries with backoff. All reads idempotent.

## 13. Backpressure
Polling intervals fixed; in-flight requests deduplicated per endpoint.

## 14. Restart recovery
Stateless client.

## 15. Configuration
`NEXT_PUBLIC_API_BASE`, `NEXT_PUBLIC_PROFILE`. Fault-injection UI renders **only** when profile is
`demo` and the user holds the `operator` role.

## 16. Security
Session/JWT; role-gated controls. **No equipment-write path exists from the browser at all** — not
merely hidden but architecturally absent, since no Operations API route reaches the Control Service.

## 17. Observability
Client error reporting; API latency from the server side.

## 18. Performance targets (TARGET — unmeasured)
First contentful paint ≤ 1.5 s; equipment list render ≤ 300 ms for 250 rows.

## 19. Test strategy
Component tests; contract tests against generated types; **a test asserting no build artifact
contains an equipment-write call**; demo controls absent outside demo profile; staleness banner
behaviour; accessibility basics (keyboard navigation, contrast, ARIA on status indicators).

## 20. Acceptance criteria
AC-009, AC-037.
