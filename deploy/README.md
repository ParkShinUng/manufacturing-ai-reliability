# Deployment Profiles

> **The implementation gate is CLOSED.** This directory is a documented placeholder. It must
> contain no executable artifact until `docs/10-human-review/v0.3/HUMAN_APPROVAL.md` records
> approval. A README stating intent is documentation; a manifest that would run is not.

Two profiles that must stay behaviourally consistent (ADR-0005).

| Subdirectory | Contents | Phase |
|---|---|---|
| `compose/` | local: 1 KRaft broker, RF=1, `min.insync=1`, private network. **Not an HA claim.** | 2-9 |
| `kubernetes/` | production-like: RF=3, `min.insync=2`, mTLS, probes, rollout | 10 |
| `helm/` | charts for the above | 10 |
| `topics/` | declarative topic manifest. **No auto-topic-creation** - an auto-created topic gets the wrong partition count and silently breaks per-equipment ordering | 3 |
