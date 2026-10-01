# Codex challenge of OD-022

> Raw output, verbatim, `codex exec -s read-only`, 2026-10-02 (the first attempt on 2026-10-01 stopped
> at a usage limit before a verdict). Verdict: **ACCEPT_WITH_CONDITIONS**, option A.

Part 1 Verdict: SOUND_WITH_CONDITIONS

Recommended option: A.

Option A is the right shape. The current conflict is real: `OPERATIONAL_DATA.md` says projections pause/resume on PostgreSQL unavailability, while §9 plus `ContractConsumer.HandleAsync` retries handler failures 3 times and then DLQs/commits them. That would create holes during a store outage, directly contradicting the projection contract.

Concrete conditions:

1. The new outage path must be a distinct, documented exception class, not “any database exception.” Only dependency-unavailable failures use it; schema-invalid, unparseable, tombstone-handler defects, and normal handler bugs keep §9 DLQ behavior.

2. The consumer must preserve partition ordering. After consuming offset N and seeing dependency outage, it must not process N+1 on that partition until N succeeds or the partition is revoked. With librdkafka semantics, `Consume` has advanced the position to N+1; retrying N requires an explicit seek back to N before re-fetch, or an equivalent pause/poll/retry design that cannot leak later records.

3. The outage loop must continue polling often enough to satisfy `max.poll.interval.ms=300000`, and must not block inside the handler or `Task.Delay` for long periods without polls.

4. Rebalance behavior must be explicit. If the partition is revoked during the outage loop, the consumer must stop retrying the held record, must not commit it, and must let the next owner resume from the last committed offset. Any `Seek` after revocation must be avoided or handled as a lost-assignment case.

5. Caught-up/staleness state must be made false as soon as a record is consumed or an outage loop begins. The current `RunAsync` only flips `CaughtUp=false` after `ProcessOneAsync` returns; an indefinite outage inside `ProcessOneAsync` could leave a previous caught-up state visible.

6. Misclassified poison-as-outage must be observable as a stalled partition: increasing lag/staleness, dependency-outage counters, and no DLQ. That visibility must be stated as the failure mode, because the tradeoff is intentional.

7. Phase 6 Safety Supervisor must explicitly adopt or constrain this path. It needs dependency-outage semantics without weakening its special rule: malformed prediction is reject-first-then-DLQ, never silent skip. It also needs documented interaction with prediction TTL, `seekToEnd`, and `SAFE_FALLBACK`.

Part 2 Numbers Challenge

1. Read retries: “none in the API; one attempt within 3 s; 503 on failure” is sound. Condition: define whether query timeout itself counts as a connection-class failure for the breaker.

2. Write retries: “projector writes use option A’s outage loop; no separate count” is sound. Condition: scope this to projection writes only, not future command/control writes.

3. Circuit breaker: 5 consecutive connection-class failures / 30 s / one half-open probe is plausible. Needs precision on failure classes, whether counts are global or per API instance, and concurrency during half-open.

4. Retention job: start plus hourly is sound. “Drop partition whose upper bound is older than retention” is the right criterion. Keep `equipment_decommission` never dropped.

5. Disk pressure: dropping it from Phase 4 is correct. A guessed data-loss threshold would be worse than a fixed retention rule; Phase 8 observability should own disk metrics and alerts.

Other findings

P4-OD22-001 Severity: P1  
`src/dotnet/EventBackbone/ContractConsumer.cs:321`  
Defect: `CaughtUp` remains whatever it was while `ProcessOneAsync` is stuck handling/retrying a consumed record. OD-022 relies on staleness/lag making outage stalls visible, but the loop can preserve a stale “caught up” truth.  
Fix: mark not caught-up immediately after a record is consumed, before handler/outage processing begins, or split consume/settle so `RunAsync` can update state before long handling.

P4-OD22-002 Severity: P1  
`docs/08-roadmap/OPEN_DECISIONS.md:1186`  
Defect: “seeks back to the record, and retries it … polling throughout” is underspecified for librdkafka. Polling while the position is past the failed record can fetch later records; polling after seek can repeatedly return the same record; rebalance can revoke the partition mid-loop.  
Fix: specify the exact algorithm: seek/pause/poll behavior, assignment check, no later offset processing, no commit until success, and abandon-on-revocation.

P4-OD22-003 Severity: P2  
`docs/08-roadmap/OPEN_DECISIONS.md:1186`  
Defect: visibility of a misclassified poison record is asserted but not made contractual.  
Fix: require metrics/logs for dependency-outage loops: topic, partition, offset, exception type, duration, retry count/time, and current lag/staleness.

P4-OD22-004 Severity: P2  
`docs/08-roadmap/OPEN_DECISIONS.md:1200`  
Defect: “connection-class failures” is not defined.  
Fix: enumerate included failures: refused connection, network timeout, authentication/authorization, pool exhaustion, query timeout, migration/SQL errors excluded or included explicitly.

Final: ACCEPT_WITH_CONDITIONS

Conditions: the seven numbered conditions above, plus fixes for P4-OD22-001 through P4-OD22-004 before OD-022 is marked resolved.
