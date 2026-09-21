# Phase 2 — Verification request to Codex

> Formal verification, read-only. `DUAL_AGENT_PROTOCOL.md` §1: Codex may recommend a fix; Claude
> implements it. Mandatory under §2 — this phase touches **protocol choice**, **OT trust
> boundaries**, **concurrency**, **retries**, **timeouts**, **failure recovery** and
> **major dependency**.
>
> `DEFINITION_OF_DONE.md` requires **P0 = 0 and P1 = 0**, and **every finding classified**.

## What was built

Commits `66aee02`, `6fc2530`, `a107b28`, `b8fc7e0`.

| Component | Files |
|---|---|
| Modbus server | `EquipmentSimulator.Protocols/ModbusTelemetryDataStore.cs`, `ModbusServerHost.cs`, `EquipmentSimulator/ModbusRegisterEncoder.cs` |
| OPC UA server | `EquipmentSimulator.Protocols/MairNodeManager.cs`, `OpcUaServerHost.cs` |
| Gateway, Modbus | `EdgeGateway/ModbusRegisterDecoder.cs`, `ModbusTelemetryClient.cs` |
| Gateway, OPC UA | `EdgeGateway/OpcUaTelemetryClient.cs`, `OpcUaSubscription.cs` |
| Normalisation | `EdgeGateway/TelemetryNormaliser.cs`, `Quality.cs`, `CanonicalTelemetry.cs` |
| Egress and loop | `EdgeGateway/EgressPort.cs`, `EquipmentPollLoop.cs`, `ReconnectBackoff.cs` |
| Checks | `tests/contract/ac_traceability.mjs`, `scripts/license-scan.mjs`, `scripts/run-load-001.mjs`, `scripts/measure-poll-vs-subscribe.mjs` |

160 unit tests pass. `validate_examples`, `consistency_check`, `safety_invariants` and
`ac_traceability` all pass.

## Decisions this phase resolved

`OD-003` two Modbus listeners · `OD-004` `SourceEpochMs` on OPC UA and the paired-signal rule ·
`OD-005` detection for Modbus, prevention for OPC UA. `ADR-0020` accepted.

## What to challenge

Cover this list, and do not stop at it.

1. **The subscription's coherence argument.** `OpcUaSubscription` assembles one sample per publish
   cycle, pairing the **k-th** queued value of each item with the k-th `SequenceNo`. Is that sound
   when items have different queue depths, or when `DiscardOldest` has dropped different numbers of
   values from different items? `_carried` also lets a value from an earlier cycle into a later
   sample — is "absent means unchanged" actually guaranteed by OPC UA, or only usually true?

2. **`OpcUaSampleCoherenceTests` is the only guard on that.** Does it actually prove atomicity, or
   does it pass for a reason unrelated to what it claims — the ticks are 120 ms apart, which may be
   slow enough to hide the race it exists to catch.

3. **AC-021 is proven on the wrong path.** `CrossProtocolTests` compares Modbus polling against the
   OPC UA **batch read**, not the subscription. The two protocols now behave differently by design
   (OD-005), so does AC-021 still mean anything, and is it testing the path that will ship?

4. **Concurrency.** `ModbusTelemetryDataStore` is shared across all slaves on a listener and reads
   `IEquipmentAccess` from NModbus worker threads. `OpcUaSubscription.OnDataChange` runs on a stack
   thread while `TryDequeue` runs on another. `MairNodeManager.Refresh` is called from a timer while
   the stack samples. Are the locks sufficient and in the right places, and is anything published
   without a barrier?

5. **The poll loop.** Absolute deadlines, backoff on read failure, reset on read success. Can it
   still spin, deadlock, or leak a task? Does re-anchoring after an overrun lose the schedule in a
   way that matters?

6. **`TelemetryNormaliser` state.** Frozen detection, the O(1) running-sum outlier window, the
   paired-signal sequence rule. Is the running sum numerically sound over a long run, and can the
   frozen counter or the trailing window be driven into a wrong state by nulls or out-of-range
   values?

7. **Did the OD resolutions land as written?** OD-003's read-only listener, OD-004's `SourceEpochMs`
   and five-case matrix, OD-005's per-protocol criteria.

8. **Do the tests prove the ACs, or only appear to?** In particular AC-001's 360 000-record check,
   AC-022's null-plus-flag rule, and AC-023's matrix. Are any tolerance bands wide enough to pass a
   wrong implementation?

9. **What is missing from Phase 2 entirely.** §17 metrics are in-process counters with no exporter;
   `equipment_state` and `fault_injections_total` are on the simulator but the gateway's own metric
   set may be incomplete. `FAIL-OT-001` has a specification but no automated test.

## Output format

`ID · severity (P0/P1/P2/P3) · file:line · what is wrong · why it matters · recommended fix`, then a
verdict line. `P0` = a safety or correctness defect in what was built. `P1` = a specification
contradiction, or a test that does not prove what it claims. Do not inflate severity, and do not
withhold a finding because the work is committed.
