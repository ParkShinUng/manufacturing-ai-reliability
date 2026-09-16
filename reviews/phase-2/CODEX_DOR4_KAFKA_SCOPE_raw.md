# Codex challenge - Phase 2 scope: where Kafka production belongs (DoR-4)

> Raw output from the `codex exec` run of 2026-09-16, verbatim. Required by
> `DUAL_AGENT_PROTOCOL.md` section 2 - the question touches distributed messaging, Kafka semantics
> and major infrastructure.
>
> Claude had drafted three options and a recommendation **without** running them past Codex first.
> The product owner caught the omission. The challenge below treats Claude's recommendation as an
> untested proposal.
>
> Outcome: Codex judged all three options wrong and proposed a fourth. Claude withdrew its
> recommendation; the product owner chose option D. Applied in `EDGE_GATEWAY.md` section 5.1,
> `ACCEPTANCE_CRITERIA.md` AC-001, and `IMPLEMENTATION_PLAN.md` phases 2 and 3.

Read-only challenge complete. My view: **all three options are wrong as currently phrased**, though **C is closest to the honest architecture**.

The premise is only partly right. “Canonical telemetry” is primarily the **data contract/shape owned by the Edge Gateway**: FR-005 says the gateway normalizes values into a canonical telemetry event, and FR-006 says every telemetry event carries required metadata. AC-001 maps to **FR-001/FR-006**, not FR-010. Kafka publication is FR-010 and is Phase 3 territory. The schema description and event contract bind that event to `factory.telemetry.v1` in the completed system, but using that to force a Phase 2 broker conflates **contract shape** with **transport realization**.

**Option A is risky.** A “minimal Kafka producer plus compose broker” quietly drags Phase 3 forward. Even a “dev broker” needs answers for topic creation, partitions, retention, auto-topic-creation, producer idempotence, `acks`, in-flight limits, delivery timeout, broker bootstrap, and failure behavior. The repo already says no auto-topic-creation because the wrong partition count breaks ordering, and Phase 3 explicitly owns pinned partitions, retention, idempotent producers, DLQ/redrive, lag metrics, and replay. So A either under-specifies Kafka and violates the topology contract, or implements a meaningful slice of Phase 3 under a Phase 2 label.

**Option B is too blunt.** Moving AC-001 again to Phase 3 would make Phase 2 unable to prove its central job: 20 equipment through gateway normalization for 30 minutes with required metadata and no gateway exception. That is exactly the bar Phase 2 should clear. It would also leave AC-022 and AC-023 awkwardly delayed even though schema validation, null dead-sensor handling, and sequence-gap detection are gateway responsibilities.

**Option C is defensible only if narrowed.** A gateway egress interface is not an “unrequested abstraction” if it represents the documented boundary where Kafka is non-control-critical and must not block the OT poll loop. But “prove AC-001 against an in-memory sink” is too loose if AC-001 keeps the word “publish” with Kafka-shaped implications. The in-memory sink should be a **test egress/recording publisher** used to prove canonical event emission, buffering behavior, and validation, not a product claim that Kafka publication is done.

My recommendation is a fourth option:

**D: split the concern explicitly.** Keep Phase 2 responsible for “gateway emits canonical telemetry records conforming to `telemetry.schema.json` for 20 machines for 30 minutes, with bounded egress buffering and no unhandled simulator/gateway exception.” Prove that with a recording/test egress sink plus failure/backpressure injection. Move only the **Kafka transport binding** proof to Phase 3: `factory.telemetry.v1`, topic creation, producer settings, idempotence, retention, partition count, broker restart, DLQ/replay-related behavior.

For AC-022 and AC-023: keep them in Phase 2. AC-022 is clearly gateway contract validation and dead-sensor/null handling. AC-023 is also gateway-level sequence-gap detection and metrics. Then Phase 3 should re-run the relevant telemetry contract checks through Kafka, but as transport integration evidence, not as the first proof that the gateway can normalize correctly.

So: **do not choose A as written.** Choose a clarified C/D: a narrow gateway egress port plus Phase 2 recording sink, with AC wording fixed so Phase 2 proves canonical telemetry **emission**, while Phase 3 proves canonical telemetry **publication to Kafka under the topology contract**.
