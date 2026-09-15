# Service Design — Observability Platform

> Prometheus + Grafana + OpenTelemetry. **CONTROL-INDEPENDENT (F32) by construction.**

## 1. Purpose
Make platform behaviour measurable, and make the reliability claims in this specification
falsifiable rather than asserted.

## 2. Responsibilities
Scrape service metrics; collect traces across prediction→decision→command; dashboards for equipment,
AI, safety, control, and platform health; alert rules.

## 3. Non-responsibilities
**Never** in the control path. Its failure must have zero control impact — if it ever does, that is
an architecture defect (F32).

## 4. Dependencies
Scrapes all services. Nothing depends on it for correctness.

## 5. Inputs / outputs
In: `/metrics` endpoints, OTLP traces. Out: dashboards, alerts.

## 6. Contracts
`docs/05-operations/OBSERVABILITY_AND_SLO.md` — the metrics contract (name, type, unit, labels,
owner) closes GAP-082.

## 7. Data ownership
Owns metrics/traces storage only. Never a source of truth for business state.

## 8. State model
Prometheus TSDB, 15 d local retention. Trace sampling: **100 % of control decisions, 1 % of the
100 ms telemetry path** — the high-frequency path would otherwise dominate trace volume while
carrying the least diagnostic value per span.

## 9. Lifecycle
Deployed alongside services; dashboards and alert rules are version-controlled in `observability/`
and diffable, not clicked together in a UI.

## 10. Normal flow
Scrape every 15 s; services emit OTLP; `correlationId` propagates across every service boundary so a
single trace spans telemetry→prediction→decision→command.

## 11. Failure behaviour
| Failure | Behaviour |
|---|---|
| Prometheus down | metrics lost for the window; **no control impact** |
| Grafana down | dashboards unavailable; no control impact |
| OTLP collector down | traces dropped; services must **not** block on export (bounded queue, drop on full) |

The last row matters: a blocking trace exporter would turn an observability outage into a control
outage, which is exactly the coupling this component must not have.

## 12. Timeout / retry / idempotency / ordering
Export timeout 5 s, bounded queue, drop-on-full. Scrape timeout 10 s.

## 13. Backpressure
Bounded exporter queues; sampling caps trace volume.

## 14. Restart recovery
Metrics resume on next scrape; gaps are visible in dashboards rather than interpolated.

## 15. Configuration
Scrape interval 15 s, retention 15 d, sampling rates, alert thresholds (from
`OBSERVABILITY_AND_SLO.md`).

## 16. Security
Read-only scraping. No secrets in labels. Dashboards behind auth in the production-like profile.

## 17. Observability (of itself)
`scrape_duration_seconds`, `up{job}`, `otel_export_failures_total`, `dropped_spans_total`.

## 18. Performance targets (TARGET — unmeasured)
Scrape overhead < 2 % CPU per service; trace export adds < 5 ms P95 to the decision path.

## 19. Test strategy
Every metric named in the contract is asserted present with the right type and labels;
**cardinality test: no metric carries `equipmentId` as a label**; alert rules unit-tested against
synthetic series; trace continuity test across all four hops.

## 20. Acceptance criteria
AC-035, AC-036.
