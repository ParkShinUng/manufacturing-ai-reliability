# Codex verification - Phase 2, round 2

> Raw output, transcribed verbatim from the `codex exec` run of 2026-09-21 (read-only; prompt pointed
> at `CLAUDE_ROUND2_CLASSIFICATION.md` and the uncommitted working tree).

Verified read-only. I ran:

`dotnet test src/dotnet/Mair.sln --no-restore` → 181 passed, 2 skipped  
`node tests/contract/validate_examples.mjs` → passed  
`node tests/contract/consistency_check.mjs` → passed  
`node tests/contract/safety_invariants.mjs` → passed  
`node tests/contract/ac_traceability.mjs` → passed  
`ilspycmd` on NModbus 3.0.83 → confirmed `ReadInputRegistersAsync` wraps blocking reads in `Task.Factory.StartNew`.

COD-P2-R2-001 · P1 · `src/dotnet/EquipmentSimulator.Protocols/MairNodeManager.cs:210` · The new OPC UA coherence key depends on `DateTime.UtcNow` being unique per model step, but the code does not enforce uniqueness. If the model loop catches up after scheduler/GC delay, multiple `Refresh` calls can happen back-to-back; two steps can share a `SourceTimestamp`. The assembler stores values by timestamp at `OpcUaSubscription.cs:236`, so a duplicate timestamp overwrites earlier node values and can fabricate or drop a sample. · This matters because AC-023’s OPC UA “zero gaps after warm-up” claim depends on timestamp-keyed assembly being a lossless coherence key under timing stress, not only under ideal 100 ms spacing. · Recommended fix: make the server’s per-equipment sample timestamp strictly monotonic and unique per published step, or key assembly on a contract-level monotonic pair such as `SequenceNo` + `SourceEpochMs` after validating all node values belong to that marker.

COD-P2-R2-002 · P1 · `src/dotnet/EdgeGateway/ModbusTelemetryClient.cs:72` · OD-006 is real and unresolved: the gateway still calls NModbus `ReadInputRegistersAsync`, which decompiles to `Task.Factory.StartNew(() => PerformReadRegisters(...))` over a blocking socket read. `WaitAsync` times out the await, not the underlying read. · This matters because the 20-equipment Modbus path can starve the thread pool and produce healthy-run read failures, overruns, and extra gaps; the Modbus AC-001 real-path test and LOAD-001 can still measure dependency behavior rather than gateway behavior. · Recommended fix: my position on OD-006 is **Option B**. Replace only the NModbus client with a genuinely async FC04 reader and keep NModbus for the simulator server. Option A is acceptable only as an interim mitigation; Option C preserves the thread-per-connection ceiling.

Round-2 coverage: COD-P2-001 through COD-P2-011 are otherwise fixed in substance: k-th pairing is gone, handles are populated before create, deterministic assembler tests exist, the shipping OPC UA source uses subscription, AC-021 covers subscription, real-path AC-001 tests exist, LOAD-001 is split per OD-005, egress is isolated and bounded, metrics export is honestly deferred to Phase 8, real endpoint outage tests exist, and OPC UA status translation is shared. CLD-P2-001 and CLD-P2-002 look correctly fixed. CLD-P2-004 is an honest Phase 2 scope reading: `protocol_connected` is the current observable, with `factory.equipment-states.v1` deferred to the egress/Kafka binding.

VERDICT: REJECT
