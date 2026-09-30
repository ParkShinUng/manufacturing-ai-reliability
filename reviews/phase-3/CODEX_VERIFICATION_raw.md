# Codex independent verification - Phase 3, round 1

> Raw output, transcribed verbatim from the `codex exec` run of 2026-09-29 (read-only; prompt
> pointed at `CLAUDE_VERIFICATION_REQUEST.md`). Verdict: **REJECT** (2 P1, 1 P2).

P3-COD-001 - P1 - src/dotnet/EventBackbone/TopicBootstrap.cs:40 - existing topics are indexed only by partition count, and `VerifyAsync` checks partitions/configs but not replication factor - AC-026 requires every topic to match the documented replication factor, so a drifted existing topic can be reported `Unchanged` while violating the topology contract - include live replica-count comparison in `VerifyAsync`, and add a drift test where an existing topic has wrong RF.

P3-COD-002 - P1 - docs/08-roadmap/IMPLEMENTATION_PLAN.md:127 - Phase 3 still lists “lag and record-age metrics” as a deliverable, and `KAFKA_EVENT_BACKBONE.md` lists `kafka_consumer_lag` / `prediction_record_age_seconds`, but I found no implementation or test coverage for those metrics - this leaves a Phase 3 deliverable incomplete and weakens the claimed lag/freshness observability - either implement and test the metrics now or move the deliverable explicitly to Phase 8 with docs reconciled.

P3-COD-003 - P2 - src/dotnet/EdgeGateway/KafkaTelemetrySink.cs:33 - the production gateway producers do not explicitly set `AllowAutoCreateTopics = false` (`KafkaEquipmentStateSink` has the same issue at line 71) - the test broker disables auto-create globally, but the producer config itself does not enforce the “topics come only from the register” rule if pointed at a permissive broker - harden both producer configs and add a production-sink negative test against an unregistered topic.

Challenge coverage: commit-after-settlement ordering looks correct for normal, handler-failure, cancellation, DLQ-failure, and rebalance cases; rebalance can cause duplicate processing, not committed loss. Poison-message classification is sound. State stream mapping, refresh timestamps, `gatewayEpoch`, `stateSequence`, tombstones, and forbidden-transition handling look consistent. Replay refusal is real for `ReplayTool`, but deeper safety still depends on later Supervisor TTL/control fencing as documented. The known OPC UA intermittent is plausibly environmental and acceptable as recorded unless it recurs. The broker-restart test proves a short outage inside the 30 s delivery budget, not broader losslessness.

Verification run: contract checks passed (`validate_examples`, `consistency_check`, `safety_invariants`, `ac_traceability`). `dotnet test src/dotnet/Mair.sln --no-restore` could not complete EventBackbone Kafka suites because this sandbox cannot access Docker’s named pipe; non-EventBackbone tests passed: 83 EquipmentSimulator tests, 114 EdgeGateway tests, 3 long-load skips. `node scripts/license-scan.mjs` was blocked by access denied on the user NuGet config before license evaluation.

REJECT
