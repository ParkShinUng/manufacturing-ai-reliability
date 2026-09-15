# Observability Configuration

> **The implementation gate is CLOSED.** This directory is a documented placeholder. It must
> contain no executable artifact until `docs/10-human-review/v0.3/HUMAN_APPROVAL.md` records
> approval. A README stating intent is documentation; a manifest that would run is not.

Prometheus scrape config, Grafana dashboards, and OTel collector config.

| Subdirectory | Contents | Phase |
|---|---|---|
| `prometheus/` | scrape config, alert rules from `OBSERVABILITY_AND_SLO.md` §3 | 8 |
| `grafana/` | the four dashboards in §6, as version-controlled JSON - diffable, not clicked together in a UI | 8 |
| `otel/` | collector config; **bounded queue, drop-on-full** so an observability outage never becomes a control outage | 8 |
