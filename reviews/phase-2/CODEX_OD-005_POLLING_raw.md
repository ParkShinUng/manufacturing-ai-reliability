# Codex challenge - equal-cadence polling cannot be lossless (OD-005, part 1)

> Raw output from the `codex exec` run of 2026-09-18, verbatim. Required by
> `DUAL_AGENT_PROTOCOL.md` section 2: protocol choice, retries, timeouts, concurrency, failure
> recovery. Claude drafted no options; only the problem and the LOAD-001 measurements were handed
> over.
>
> The correction that mattered: Claude argued "OPC UA buffers, Modbus cannot", and Codex found that
> the OPC UA client **was not subscribing at all** - it used a batch read through the same polling
> abstraction, so section 1.3 was entirely unimplemented and the comparison had never been made.
>
> Codex also refused to let the remaining loss be written off as structural: the SlaveExceptions,
> reconnects and loop overruns were separate findings that had to be explained first.

**Challenge Result**

Claude’s analysis is mostly correct at the Modbus contract level, but incomplete for this implementation.

Modbus polling reads a latest-value register image. `OT_PROTOCOL_MAPPING.md` §2.7 requires one 23-register block read every 100 ms, but nothing in that contract prevents an equipment tick from overwriting an unread prior value. `TelemetryNormaliser` correctly reports that as `SEQUENCE_GAP`; it cannot recover the missed sample. `EDGE_GATEWAY.md` §14 and AC-023 require detection, not prevention.

The key correction: the current OPC UA implementation does **not** use the §1.3 subscription/queue design. [OpcUaTelemetryClient.cs](/C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/src/dotnet/EdgeGateway/OpcUaTelemetryClient.cs) uses batch `ReadValuesAsync`, and [EquipmentPollLoop.cs](/C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/src/dotnet/EdgeGateway/EquipmentPollLoop.cs) treats OPC UA through the same `ReadAsync` polling abstraction. So Claude’s “OPC UA buffers, Modbus cannot” claim is right for the documented intended design, but not for the code currently inspected.

**Is 6.5% loss phase drift alone?**

Not honestly. Equal-period producer/poller phase drift can absolutely create duplicate-read plus skipped-sample patterns, especially with Windows timer jitter. The first run improving from 3569/4000 and 393 gaps to 3917/4000 and 267 gaps after absolute deadlines supports that.

But the second report also has `40` loop overruns, `30` reconnects, and `30` `SlaveException` read failures. Phase drift does not explain Modbus slave exceptions. Some loss is structural timing behavior; some is a further defect or harness artifact. One visible artifact: load loops start before the model ticker has necessarily produced the first sample, and `Latest()` returning null becomes `SlaveDeviceFailure`.

**Options**

1. **Keep AC/LOAD strict and change Modbus design to make zero gaps possible.**  
   Add a per-equipment sample queue/history on the simulator Modbus side, or poll faster than production and de-duplicate. This stops pretending latest-register polling is lossless, but it makes the simulator less like ordinary Modbus register polling.

2. **Keep Modbus latest-register semantics and change acceptance criteria.**  
   AC-023 should require “no dropped sample goes undetected.” LOAD-001 should allow bounded, reported Modbus gaps under real-time polling and assert zero silent loss, zero buffer drops, bounded gap rate, and no protocol read failures. This is the most honest if Modbus realism matters.

3. **Use OPC UA subscription as the lossless path, treat Modbus as lossy-by-design.**  
   Implement actual monitored-item subscriptions with queue size 10 for OPC UA, and explicitly state that “no gaps” applies to queued protocols only. Consequence: AC-021 cannot mean identical temporal completeness across protocols; only value mapping and gap reporting can match.

4. **Phase-lock the gateway to simulator ticks.**  
   The harness could expose a tick event or ready barrier and drive polls immediately after model updates. This can pass the demo, but it proves a synchronized lab setup, not an OT polling design.

**My choice**

Choose option 2, plus fix the implementation/harness defects separately. The contract should not forbid reported Modbus gaps when the selected Modbus design is latest-register polling at the same cadence as the producer. That is not a gateway bug; it is the behavior `sequence` exists to reveal.

But do not accept the current LOAD-001 result as “just Modbus being Modbus.” The `SlaveException`/reconnects and loop overruns are separate findings and should be fixed or explained before setting any bounded-loss threshold.
