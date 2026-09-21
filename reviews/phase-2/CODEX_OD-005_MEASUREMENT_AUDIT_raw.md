# Codex audit of the poll-vs-subscribe measurement (OD-005, part 2)

> Raw output from the `codex exec resume` run of 2026-09-21, verbatim; same session as
> [`CODEX_OD-005_POLLING_raw.md`](CODEX_OD-005_POLLING_raw.md).
>
> The product owner deferred the decision until the section 1.3 subscription existed and the claim
> had been measured. Codex was asked to audit the methodology **before** trusting the numbers,
> because Claude had got the measurement wrong twice.
>
> It accepted the coverage result and flagged what it did not prove: assembling from latest-known
> values could pair a marker from tick N with a measurement from N-1. That became
> `OpcUaSampleCoherenceTests`, which failed on its first run and forced the assembly to be rewritten
> around publish cycles.

**Audit Take**

The measurement is much sounder for the question it is actually answering: “did the client receive distinct source sequence values?” Counting distinct `Sequence` values fixes the inflated Modbus number, and using `SequenceNo` as the subscription tick marker is the right direction because `State` is change-silent while `SequenceNo` changes every tick.

But I would qualify it:

- For **sequence coverage and gap detection**, the methodology is sound enough. The subscribed path receiving 599 distinct sequences with 0 gaps is meaningful.
- For **exact multi-node sample coherence**, `OpcUaSubscription` is not fully proven. In [OpcUaSubscription.cs](/C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/src/dotnet/EdgeGateway/OpcUaSubscription.cs), assembling from latest-known values is legitimate for nodes that do not change, like `State`, but it can mix `SequenceNo` from tick N with measurement values from N-1 or N+1 if monitored-item callbacks are delivered in a different order. That is not fatal to this coverage measurement, but it is not a proof that every emitted canonical sample is an atomic source snapshot.
- The harness gives OPC UA the intended advantage, not an unfair one: server-side sampling/queueing is exactly the §1.3 design. The only reproducibility gap I saw is that [PollVsSubscribeTests.cs](/C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/src/dotnet/EdgeGateway.Tests/PollVsSubscribeTests.cs) references `scripts/measure-poll-vs-subscribe.mjs`, but that script is not present in this checkout.

**Phase Drift**

Yes: 439 distinct out of 601 records, with 162 duplicate reads and 161 gaps, is almost textbook equal-cadence phase drift. The poller emitted the same number of records as samples produced, but about 162 polls landed in already-read intervals, balanced by about 162 source ticks overwritten before the next useful poll. That pattern does not need another defect to explain it.

**Decision**

This measurement strengthens, not weakens, my prior recommendation.

AC-023 should remain a **detection** criterion, not a universal prevention criterion. It should say that any observed discontinuity within a continuous source epoch must produce `SEQUENCE_GAP` and increment the metric. It should explicitly distinguish protocol capability:

- OPC UA subscription path: expected to prevent normal scheduler-cycle loss through server-side queueing; acceptance may require zero sequence gaps after startup warmup.
- Modbus latest-register polling: not lossless by construction; acceptance requires no silent loss, not no loss.

LOAD-001 should change. “No dropped telemetry, no gaps” is wrong for Modbus polling at the same cadence as production. It should require:

- report `records`, `distinct source sequences`, `duplicates`, `coverage`, `sequence gaps`, `read failures`;
- OPC UA subscription: zero gaps after startup, bounded startup incomplete groups, no buffer drops;
- Modbus polling: every skipped sequence is flagged, duplicates are counted, no healthy-run read failures, and coverage is measured/reported rather than pretending latest-register polling is lossless.

I would also add one follow-up test for OPC UA sample coherence: for changing nodes, verify assembled values carry the same source timestamp or source epoch as the `SequenceNo` marker, while allowing stable nodes like `State` to remain latest-known. That closes the last “is this a real sample?” loophole.
