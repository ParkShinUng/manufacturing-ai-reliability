# Codex challenge of the Operations API read-model mapping

> Raw output, verbatim, `codex exec -s read-only`, 2026-10-01.

# Round 1 - verdict REVISE

P4-M-001 · P1 · docs/11-service-design/OPERATIONS_API.md:105 · `fallbackRatePct` uses `null` for PostgreSQL unavailable · OD-012 defines `null` as “no record observed,” while this mapping makes it mean “dependency unavailable”; ADR-0024 C02 says `/platform/health` answers without PostgreSQL with unavailable dependencies as field values, not by reusing no-record semantics · define a separate health/dependency field or omit `fallbackRatePct` when not measurable, but do not cite OD-012 for DB outage.

P4-M-002 · P1 · docs/11-service-design/OPERATIONS_API.md:87 · keyset paging is underspecified for concurrent inserts · AUDIT-001 requires paging to return every matching audit record once while inserts keep arriving, but a cursor containing only the last row sort key can miss newly inserted rows newer than page 1, and `id` is not an actual column name in the migrations · pin an upper-bound snapshot/as-of key on page 1 and define `id` as `decision_id` or `command_id`, including descending seek predicates.

P4-M-003 · P1 · docs/11-service-design/OPERATIONS_API.md:92 · trace collapses multiple records per correlation ID to “latest” · AC-047/TRACE-001 says the trace returns the stored chain; if retries, rejected/expired outcomes, or repeated decisions share a correlation ID, choosing only latest hides audit-relevant records · either prove cardinality is one decision and one outcome per correlation ID in the contracts, or return arrays / deterministic all-record summaries.

P4-M-004 · P2 · docs/11-service-design/OPERATIONS_API.md:73 · `activeReasonCodes` overclaims the meaning of `reasonCodes` · the safety-decision schema defines `reasonCodes` as diagnostic decision codes, including `AI_ACCEPTED`; “active” implies an in-force condition, but the latest decision may be accepted, superseded, or no longer reflected in Control Service mode · rename/describe as latest decision reason codes, or derive active conditions from current mode/outcome if that is the intended field.

P4-M-005 · P2 · docs/11-service-design/OPERATIONS_API.md:63 · decisions/commands existence check is tied only to state history · the request asks for decisions/commands to use “any history,” and AC-029 is about retained/queryable audit records; returning `404` when there is no state history but there are decision/outcome rows would hide audit · define `404` for audit routes as no history in state, decision, or outcome tables for that equipment, or return empty `200` for known-by-audit equipment.

P4-M-006 · P2 · docs/11-service-design/OPERATIONS_API.md:101 · `kafkaReachable` is not actually Kafka reachability · “every projector consumer caught up in the last 30 s” is projector freshness/lag health, not broker reachability; a reachable broker with a stuck projector and an unreachable broker on a quiet topic can be misnamed · rename to `projectorsCaughtUp`/`projectionFresh`, or define `kafkaReachable` from an actual broker/admin/consumer connectivity check.

P4-M-007 · P3 · docs/11-service-design/OPERATIONS_API.md:60 · equipment current view relies on offset comparison but the API mapping does not restate the partition/key condition · OD-021 makes offset comparison valid only because state and tombstone share the equipment key and partition; without saying that here, implementers could compare offsets across partitions · add the OD-021 condition or point directly to `current_equipment_state`.

REVISE

## Claude classification

- P4-M-001 ACCEPTED: fallbackRatePct omitted when unmeasurable, not null.
- P4-M-002 PARTIALLY_ACCEPTED: descending keyset already returns every row that existed at page 1 exactly once; rows arriving later appear at most once. The guarantee is now stated precisely and `id` is the real column name. No snapshot key added - it would exclude nothing keyset does not.
- P4-M-003 ACCEPTED: decisions and commands are arrays.
- P4-M-004 ACCEPTED: activeReasonCodes only for REJECT or FALLBACK.
- P4-M-005 ACCEPTED: 404 on audit routes only with no row in state, decision or outcome tables.
- P4-M-006 ACCEPTED: kafkaReachable is a metadata request, 2 s timeout, cached 5 s.
- P4-M-007 ACCEPTED: the mapping points at current_equipment_state and restates its partition condition.

# Round 2 - verdict ACCEPT_WITH_CONDITIONS

P4-M-001 CLOSED.

P4-M-002 OPEN. Claude’s algorithm reasoning is correct: a descending keyset cursor does not need a snapshot upper bound to return every row that existed at page 1 exactly once. Rows inserted later with newer keys are intentionally outside that traversal. However, [TEST_SPECIFICATIONS.md](/C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/docs/06-development/TEST_SPECIFICATIONS.md:422) still says “returns every record once even with inserts arriving between pages” without the page-1 existence bound, so the acceptance test can still demand live-tail completeness. The partial acceptance is conceptually right, but the docs are not fully reconciled.

P4-M-003 CLOSED.

P4-M-004 CLOSED.

P4-M-005 CLOSED.

P4-M-006 CLOSED.

P4-M-007 CLOSED.

No new P0/P1 findings.

ACCEPT_WITH_CONDITIONS:
- Tighten `AUDIT-001` to match the new paging guarantee: every row that existed when page 1 was served is returned exactly once; later inserts may appear once or not at all depending on their key relative to the cursor, never twice.

Condition applied: AUDIT-001 now states the page-1 existence bound.
