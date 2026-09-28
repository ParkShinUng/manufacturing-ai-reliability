# Phase 3 Definition-of-Ready — challenge request to Codex

> Read-only challenge, before any Phase 3 work and before any option is drafted. `DUAL_AGENT_PROTOCOL.md`
> §1: Codex challenges, Claude implements, the human decides. Phase 2's `DoR-4` went the other way —
> options were written first and the premise under them turned out to be wrong — so this asks the
> questions before proposing anything.
>
> Phase 3 has **not** been requested by the product owner. This is the readiness check only.

## What Phase 3 is

`IMPLEMENTATION_PLAN.md`: 7 topics with pinned partitions, retention and compaction · idempotent
producers · manual offset commit · DLQ and redrive · lag and record-age metrics · replay on
projector groups only · **binding the gateway's egress port to `factory.telemetry.v1` and
`factory.equipment-states.v1`** (FR-010). Proof: **AC-003, AC-026, AC-027**.

Phase 2 is complete: the gateway emits canonical telemetry through the egress port
(`EDGE_GATEWAY.md` §5.1), Codex-verified over three rounds, `LOAD-001` passed.

## What the readiness check already found, and is not asking about

These look like ordinary work items rather than questions, and are listed so you do not spend effort
re-finding them — challenge them only if the classification is wrong:

- No accepted ADR selects the **Kafka client library** or the local broker runtime. ADR-0001 chose
  Kafka as the architecture, not a dependency. This needs its own ADR and its own challenge.
- **AC-026** and **AC-027** have no test specification; they sit in `TEST_SPECIFICATIONS.md` §7a,
  whose rule fails the build once Phase 3 starts.

## What to challenge

1. **`AC-003` may be unprovable in Phase 3.** It requires that a replayed Kafka range rebuilds
   **read models** identically, and that the Safety Supervisor issues no command as a result.
   PostgreSQL projections and read models are **Phase 4** deliverables; the Supervisor is **Phase 6**.
   Is AC-003 mis-assigned, is the phase list wrong, or is there a reading under which Phase 3 can
   prove it? What is the smallest honest Phase 3 obligation here?

2. **`stateSequence` has no restart semantics.** `EVENT_CONTRACTS.md` §2 and `DEC-007` assign it to
   the gateway, per `equipmentId`. `KAFKA_TOPOLOGY_AND_SEMANTICS.md` §7 makes
   `(equipmentId, stateSequence)` the **duplicate identity** for `factory.equipment-states.v1`.
   Nothing states what happens to the counter when the gateway process restarts. Does the contract
   as written already constrain this? What breaks under each possible answer — deduplication, the
   compacted topic, the Supervisor's earliest-to-caught-up startup read (`SAFETY_SUPERVISOR.md` §83)?
   Is this the same class of defect as OD-004's paired-signal problem, where two signals disagreeing
   silently is the real failure?

3. **No rule says when an equipment-state record is emitted.** `EDGE_GATEWAY.md` §2 says the gateway
   publishes gateway-**observed** state; `EQUIPMENT_MODEL_AND_STATE.md` §3 defines the states and
   transitions T1–T12. Is "one record per observed transition" actually sufficient, given the topic
   is `compact+delete` with 7 d retention and the Supervisor's gate 10 rejects **stale** state? Does
   a consumer that starts after retention has deleted a long-stable equipment's only record end up
   with no state at all — and if so, is that a contract defect rather than an implementation choice?

4. **Anything else in the Phase 3 contracts that cannot be implemented as written.** The
   Phase 2 precedent is `OD-003`: the ADR challenge found that the contract named one Modbus port
   for two incompatible purposes, and that was a document defect no implementation could resolve.
   The equivalents here would be ordering preconditions (§4), the replay policy (§5), offset commit
   semantics (§6), DLQ headers (§9), backpressure (§10), retry/timeout profiles (§11).

## Output format

`ID · severity (P0/P1/P2/P3) · file:line · what is wrong or unanswerable · why it matters · what
must be decided or fixed before Phase 3 starts`, then a readiness line: **READY** or **NOT READY**
with the blocking IDs.

Do not propose an implementation. Where a decision is needed, say what has to be decided and what
each answer costs — the product owner decides, not the agents.
