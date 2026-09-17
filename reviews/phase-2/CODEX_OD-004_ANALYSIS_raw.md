# Codex challenge - OPC UA has no restart signal (OD-004, part 1)

> Raw output from the `codex exec` run of 2026-09-16, verbatim. Required by
> `DUAL_AGENT_PROTOCOL.md` section 2: protocol choice, schema versioning, failure recovery.
>
> At the product owner's instruction Claude drafted **no options** before this challenge - only the
> problem was handed over. Claude's own hypothesis, that OPC UA `ServerStatus.StartTime` was the
> answer, was rejected here with a specific reason Claude had not accounted for: it is scoped to the
> server process, and one simulator server hosts many equipment.
>
> This challenge also surfaced a second defect nobody had noticed - `TIME_AND_DATA_QUALITY.md` and
> `EDGE_GATEWAY.md` said opposite things about an epoch reset.

**Challenge Result**

Yes, the asymmetry reading is correct. In [OT_PROTOCOL_MAPPING.md](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/docs/03-contracts/OT_PROTOCOL_MAPPING.md:33) section 1.2 defines ten OPC UA nodes: seven measurements, state, `SequenceNo`, and writable setpoint. There is no `sourceEpochMs`, restart counter, boot ID, uptime, or equivalent. Section 1.4 only maps OPC UA `StatusCode` to quality flags; it does not cover restart identity. The implemented simulator address space also says “Ten nodes per equipment” and exposes `SequenceNo` but no epoch signal in [MairNodeManager.cs](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/src/dotnet/EquipmentSimulator.Protocols/MairNodeManager.cs:6).

OPC UA itself provides related but insufficient signals:

- `SourceTimestamp` is value timing, and the repo already contracts it as `eventTimeUtc`. OPC UA defines it as the timestamp applied by the data source to a value instance, not as equipment uptime or restart identity. Using it as `sourceEpochMs` would invent behavior.
- `ServerStatus.StartTime` is standard OPC UA, but it is the server process start time. OPC UA Part 5 says it is constant for the server instance and records when that server process started. In this project one OPC UA server hosts multiple equipment instances, so `ServerStatus.StartTime` is not per-equipment restart evidence.
- OPC UA subscription sequence numbers / queue overflow semantics can indicate OPC UA publish or monitored-item loss, not equipment sequence reset cause. They do not distinguish equipment restart from telemetry gap.

Sources: OPC Foundation on [`SourceTimestamp`](https://reference.opcfoundation.org/specs/OPC-10000-4/7.11.3), [`ServerStatusDataType.startTime`](https://reference.opcfoundation.org/Core/Part5/v104/docs/12.10), and [`ServerStatusType.StartTime`](https://reference.opcfoundation.org/Core/Part5/v105/docs/7.6).

**Contract Problem**

This is not merely an implementation choice. The contract is wrong or incomplete.

Evidence:

- Modbus explicitly carries `sourceEpochMs` at `+20` in [OT_PROTOCOL_MAPPING.md](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/docs/03-contracts/OT_PROTOCOL_MAPPING.md:155).
- Modbus section 2.6 says `sourceEpochMs` allows frozen/restarted-device detection in [OT_PROTOCOL_MAPPING.md](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/docs/03-contracts/OT_PROTOCOL_MAPPING.md:226).
- Edge Gateway section 14 says a first sample after restart raises `SEQUENCE_GAP` unless `sourceEpochMs` also reset in [EDGE_GATEWAY.md](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/docs/11-service-design/EDGE_GATEWAY.md:113).
- The normaliser requires `sourceEpochMs` even on its OPC UA path in [TelemetryNormaliser.cs](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/src/dotnet/EdgeGateway/TelemetryNormaliser.cs:101), but the OPC UA contract provides no source for that argument.
- `TIME_AND_DATA_QUALITY.md` adds another contradiction: it says equipment clock reset raises `SEQUENCE_GAP`, while Edge Gateway and the normaliser say sequence reset plus epoch reset is not a gap.

**Impact**

AC-021: If scoped narrowly to “engineering values,” restart detection does not break scaling/value equality. But canonical telemetry would diverge in quality flags after restart: Modbus can suppress `SEQUENCE_GAP` when sequence and epoch reset together; OPC UA cannot make that determination from the contracted nodes. So the broader “same equipment, same canonical event semantics” claim is not satisfiable.

AC-023: The current contract cannot honestly prove “no gap goes undetected” across both protocols. OPC UA can detect `sequence - previous > 1`, but on sequence rollback it cannot distinguish restart from protocol loss/corruption. Either it false-positives `SEQUENCE_GAP` on valid restarts or it risks missing a real discontinuity.

**Options**

1. **Add `SourceEpochMs` or `RestartCounter/BootId` to OPC UA per-equipment address space.**  
   Best option. Makes OPC UA and Modbus semantically equivalent, matches the existing normaliser shape, and preserves AC-023. This is a contract/schema change to `OT_PROTOCOL_MAPPING.md`, simulator OPC UA address space, OPC UA client, tests, and likely an ADR/decision record.

2. **Use OPC UA `ServerStatus.StartTime`.**  
   I would reject this. It is server-scoped, not equipment-scoped, and would be wrong for multi-equipment servers or server restarts unrelated to equipment restarts.

3. **Use OPC UA `SourceTimestamp` as restart signal.**  
   Reject. It is already contracted as event time. A wall-clock timestamp may continue monotonically across equipment restart and does not encode “milliseconds since equipment start.”

4. **For OPC UA, treat every sequence rollback as `SEQUENCE_GAP`.**  
   Conservative for AC-023 but creates protocol disagreement after normal restarts. Modbus says “not a gap”; OPC UA says “gap.” This weakens AC-021/canonical equivalence and creates noisy false positives.

5. **For OPC UA, reset tracking silently on sequence rollback.**  
   Reject. This avoids false positives but violates the spirit of AC-023 because a real discontinuity can disappear.

6. **Narrow the contract: restart/gap disambiguation is guaranteed only for Modbus.**  
   Honest but weak. It requires changing AC-023 and probably AC-021 expectations. I would choose this only if the project intentionally wants OPC UA to be less capable.

My recommendation: change the contract, preferably by adding a per-equipment OPC UA `SourceEpochMs` node matching the Modbus field. The current contract asks the OPC UA client to evaluate a condition for which the contracted OPC UA address space does not provide the necessary input.
