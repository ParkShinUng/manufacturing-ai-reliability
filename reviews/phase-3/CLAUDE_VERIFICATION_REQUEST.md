# Phase 3 — Verification request to Codex

> Formal verification, read-only. `DUAL_AGENT_PROTOCOL.md` §1: Codex may recommend a fix; Claude
> implements it. Mandatory under §2 — this phase touches **distributed messaging**, **Kafka
> semantics**, **retries**, **timeouts**, **idempotency**, **schema versioning**, **major
> dependency** and **major infrastructure**.
>
> `DEFINITION_OF_DONE.md` requires **P0 = 0 and P1 = 0**, and **every finding classified**.

## What was built

Commits `5dfb515` … `49296bc` (`git log --oneline 5a53c00..HEAD`).

| Component | Files |
|---|---|
| Topic register and bootstrap | `EventBackbone/TopicRegister.cs`, `TopicBootstrap.cs` |
| Shared consume-validate-DLQ path | `EventBackbone/ContractConsumer.cs`, `ContractSchema.cs` |
| Replay | `EventBackbone/Replay.cs` |
| Gateway egress on Kafka | `EdgeGateway/KafkaTelemetrySink.cs`, `TelemetryJson.cs`, `EquipmentStateStream.cs`, `KafkaEquipmentStateSink.cs`, changes in `EquipmentPollLoop.cs` |
| Tests | `EventBackbone.Tests/*`, `EdgeGateway.Tests/PollLoopTests.cs` |
| Tooling | `scripts/license-scan.mjs` (now a gate) + `scripts/license-evidence.json`, `.github/workflows/ci.yml`, `.nvmrc` |

272 tests pass locally; two CI runs on `ubuntu-24.04` were green. Contract checks and the licence
gate pass.

## Decisions this phase took

`OD-007` (AC-003 split into AC-045 / AC-046 / AC-003) · `OD-008` (state stream: 2 s refresh,
`gatewayEpoch`, compact-only, tombstones) · `OD-009` (one bounded shared consumer) · `OD-010`
(no inferred `activeConditions`, T13–T16, `aiEligible` required) · `ADR-0021` (Confluent.Kafka,
apache/kafka 4.3.1, Testcontainers core — amended twice) · `ADR-0022` (Corvus.Text.Json.Validator,
after the first recommendation was rejected) · `ADR-0023` (CI).

## What to challenge

Cover this list, and do not stop at it.

1. **Commit ordering in `ContractConsumer`.** The claim is that a record's offset is committed only
   once it has settled — handled, or on the DLQ. Is that true on every path, including a handler
   that throws `OperationCanceledException`, a DLQ produce that throws, and a rebalance mid-record?
   Can a record be committed past without being settled, or settled twice in a way that matters?
2. **The poison-message path.** Schema-invalid and unparseable input go to the DLQ on the first
   attempt; handler failures get three attempts on §9's schedule. Is the classification right, and
   is `SchemaVerdict` ever wrong about which one it is?
3. **Non-blocking egress.** `KafkaTelemetrySink` and `KafkaEquipmentStateSink` rely on `Produce`
   enqueueing locally. When can `Produce` block rather than throw, and does the poll loop's
   peek-emit-dequeue actually keep the record in every failure mode? What is lost, and is it counted?
4. **The equipment-state stream.** The observed-state rules, T1–T16 mapping, refresh cadence, the
   connect timeout, reconnection through CONNECTING, `stateSequence` and `gatewayEpoch`. Is there a
   sequence of observations that produces a record the contract forbids, a forbidden transition, or
   a stale state gate 10 would misread?
5. **The replay mechanics.** `ReplayTool`'s refusal, the register, and `seekToEndOnAssignment`. Is
   the Supervisor's protection real at this layer, or does it depend on callers using the tool?
6. **Topic bootstrap.** Idempotence, the refusal to mutate, `min.insync.replicas`, the DLQ topics.
   Anything a second run could change, or a drift it would miss?
7. **Do the tests prove the ACs, or only appear to?** Especially AC-027's claim that the *production*
   path is exercised, AC-045's "exactly the range", and the broker-restart test's claim that nothing
   is lost. Are any timing bounds loose enough to pass a wrong implementation?
8. **The known intermittent** recorded in `TEST_SPECIFICATIONS.md` (OPC UA subscription real-path
   test, 1 failure in 11 full runs, cause unknown). Is it plausibly caused by Phase 3, and is
   recording it rather than fixing it acceptable under this repository's rules?
9. **Documents against code.** Anything `KAFKA_TOPOLOGY_AND_SEMANTICS.md`, `EDGE_GATEWAY.md`,
   `KAFKA_EVENT_BACKBONE.md`, the ADRs or the schemas state that the code does not do — or the
   reverse.
10. **What is missing from Phase 3** against `IMPLEMENTATION_PLAN.md`'s deliverables — "lag and
    record-age metrics" in particular.

## Output format

`ID · severity (P0/P1/P2/P3) · file:line · what is wrong · why it matters · recommended fix`, then a
verdict line. `P0` = a safety or correctness defect in what was built. `P1` = a specification
contradiction, or a test that does not prove what it claims. Do not inflate severity, and do not
withhold a finding because the work is committed.
