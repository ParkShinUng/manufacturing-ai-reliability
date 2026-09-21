# Codex independent verification - Phase 2 (commits 66aee02..b8fc7e0)

> Raw output, transcribed verbatim from the `codex exec` run of 2026-09-21 (read-only sandbox use,
> prompt pointed at `CLAUDE_VERIFICATION_REQUEST.md`).

Verification run read-only. Allowed checks passed:

`dotnet test src/dotnet/Mair.sln --no-restore`: 160 passed, 2 skipped.  
`validate_examples`, `consistency_check`, `safety_invariants`, `ac_traceability`: all passed.

Findings:

COD-P2-001 · P0 · `src/dotnet/EdgeGateway/OpcUaSubscription.cs:171` · The subscription assembles samples by pairing the k-th queued value of each node with the k-th `SequenceNo`, falling back to later/earlier carried values at line 197. OPC UA does not guarantee equal queue depth or equal discard count per monitored item, so this can fabricate a sample that never existed. · This is telemetry correctness on the path that AC-023 relies on for zero gaps; a fabricated sample can mask data loss or feed bad data downstream. · Assemble by a defensible coherence key, or treat unequal queued counts/discards as incomplete/lossy and flag/drop instead of stitching.

COD-P2-002 · P1 · `src/dotnet/EdgeGateway/OpcUaSubscription.cs:127` · `_handles` is populated after `CreateAsync`; a data-change callback can arrive while `_handles` is still empty or being mutated. · This can drop initial notifications and creates an unsynchronised `Dictionary` read/write race between stack callback and startup code. · Populate immutable handle metadata before enabling publishing, or guard all handle access with the same lock.

COD-P2-003 · P1 · `src/dotnet/EdgeGateway.Tests/OpcUaSampleCoherenceTests.cs:94` · The coherence test ticks every 120 ms, slower than the 100 ms publishing interval, so it mostly avoids multi-value queues and discard-oldest pressure. · It does not prove the k-th pairing logic that it is meant to validate. · Add a deterministic stress test where several server samples accumulate in one publish cycle, with asymmetric changed/unchanged nodes and queue overflow.

COD-P2-004 · P1 · `src/dotnet/EdgeGateway/EquipmentPollLoop.cs:238` · The OPC UA `ITelemetrySource` adapter still calls `client.ReadAsync`, not `SubscribeAsync`. · The documented Phase 2 gateway path is OPC UA subscription plus Modbus polling; the shipping adapter bypasses the subscription behavior AC-023 depends on. · Add an `OpcUaSubscriptionTelemetrySource` that drains subscription frames, and use it as the gateway OPC UA path.

COD-P2-005 · P1 · `src/dotnet/EdgeGateway.Tests/CrossProtocolTests.cs:118` · AC-021 cross-protocol agreement compares Modbus against OPC UA batch read, not the subscription path. · It can pass while the actual documented OPC UA path has coherence, quality, or timestamp defects. · Re-run AC-021 against subscription-produced canonical telemetry, not only batch reads.

COD-P2-006 · P1 · `src/dotnet/EdgeGateway.Tests/SustainedEmissionTests.cs:85` · AC-001’s 360,000-record check directly encodes/decodes simulator samples in-process and bypasses real protocol servers, clients, poll loops, reconnect, and subscription. · It proves canonical shape at scale, but not “Edge Gateway emits” through the Phase 2 path. · Keep this as a fast unit/property test, but add an AC-001 integration or load test through the real gateway paths.

COD-P2-007 · P1 · `src/dotnet/EdgeGateway.Tests/LoadScenarioTests.cs:106` · LOAD-001 runs Modbus only and asserts zero sequence gaps at line 207. · OD-005 explicitly changed Modbus to detection-only; this assertion is now testing the wrong requirement, while OPC UA subscription prevention is not asserted. · Split LOAD-001 per the matrix: Modbus requires every skipped sequence flagged; OPC UA subscription requires zero gaps after warm-up.

COD-P2-008 · P1 · `src/dotnet/EdgeGateway/EquipmentPollLoop.cs:99` · Sink emission is inside the same `try` as protocol reads; if `_sink.Emit` throws in `Drain` at line 160, the broad catch marks `ProtocolConnected=false` and backs off. · A publish-side failure can throttle or stop OT polling, contradicting the non-control-critical egress rule. · Isolate egress exceptions from protocol health; count publish errors/drops and keep sampling.

COD-P2-009 · P1 · `src/dotnet/EdgeGateway/TelemetryNormaliser.cs:68` · AC-023 requires incrementing `telemetry_sequence_gaps_total`, but Phase 2 only exposes an in-process `SequenceGaps` property and no gateway metric exporter/metric port for the §17 metric set. · The acceptance criterion is only partially implemented and cannot drive alerts/observability. · Add the metric surface or explicitly defer/export-bound metrics in the Phase 2 docs and AC mapping.

COD-P2-010 · P1 · `docs/06-development/TEST_SPECIFICATIONS.md:100` · `FAIL-OT-001` specifies real endpoint disconnect/restore, but the implemented tests use fake source failures or client reconnect calls, not stopping/restoring the OPC UA endpoint or Modbus read-only listener. · AC-002/R-01 is not proven on the protocol stack. · Add an automated failure test that tears down/restores the endpoint and verifies reconnect, event resumption, and gap flagging.

COD-P2-011 · P2 · `src/dotnet/EdgeGateway/OpcUaSubscription.cs:254` · Subscription status translation omits the `GoodLocalOverride → OUTLIER_SUSPECTED` handling present in `OpcUaTelemetryClient.ReadAsync`. · Batch and subscription quality semantics can diverge despite the comment saying they are identical. · Share one translation helper or add the missing status case and tests.

VERDICT: REJECT
