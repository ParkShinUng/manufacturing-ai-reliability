# Codex verification - the applied OD-007 / OD-008 / OD-009 decisions

> Raw output, transcribed verbatim from the `codex exec` run of 2026-09-28 (read-only), after the
> decisions were applied to the documents. Verdict: ACCEPT_WITH_CONDITIONS; all four conditions
> were then applied.

OD009-01 - P1 - `docs/01-requirements/ACCEPTANCE_CRITERIA.md:73` - AC-027 still only says schema-invalid records go to DLQ on first attempt with headers/redrive - why it matters: OD-009’s condition was stricter: Phase 3 must own the production consume-validate-DLQ component, later consumers must use it, offsets commit only after DLQ produce succeeds, downstream processing must not run, and the test consumer must exercise that component rather than a test-only path - recommended fix: amend AC-027 and add its test spec to assert the shared production component path, post-DLQ offset commit, full headers, no downstream processing, and no automatic redrive.

OD009-02 - P1 - `docs/11-service-design/KAFKA_EVENT_BACKBONE.md:10` - the Kafka service design still lists only broker/topic responsibilities and does not make the shared consume-validate-DLQ component a Phase 3 responsibility - why it matters: the OD-009 condition is currently mostly recorded in `OPEN_DECISIONS.md`, not in the service design an implementer will build from - recommended fix: add the bounded component to responsibilities/non-responsibilities/test strategy, explicitly limited to `KAFKA_TOPOLOGY_AND_SEMANTICS.md` §9 plus post-DLQ commit semantics.

OD007-01 - P2 - `docs/11-service-design/KAFKA_EVENT_BACKBONE.md:80` - Phase 3 test strategy says AC-045 proves “projector groups replay” - why it matters: projectors are Phase 4; OD-007/AC-045 intentionally made Phase 3 prove replay mechanics without needing projections/read models - recommended fix: say “a replay-eligible consumer group” here, matching AC-045 and `FAIL-KAFKA-001`.

DEC007-01 - P3 - `docs/09-decisions/DEC-007-contract-precedence.md:42` - DEC-007’s sequence summary still names only `stateSequence` for equipment-state records - why it matters: after OD-008, the safe identity is paired with `gatewayEpoch`; leaving the old accepted decision summary unreconciled can mislead future schema work - recommended fix: add a dated note that OD-008 amends equipment-state identity to `(equipmentId, gatewayEpoch, stateSequence)` while retaining `stateSequence`’s gateway-assigned scope.

I found the OD-007 F11 ownership/numeric AC split implemented: AC-003 owns the Phase 6 F11 claim, AC-045/AC-046 are numeric and visible to `ac_traceability.mjs`, and their specs assert the claimed mechanics/read-model rebuild behavior. OD-008 is substantively implemented: 2 s refresh plus the budget inequality, `gatewayEpoch` required in schema and identity, compact-only retention, tombstone rule, and observation-time freshness semantics are present.

On the v1 schema amendment: adding required `gatewayEpoch` to `contracts/jsonschema/v1/equipment-state.schema.json` is honest and sufficient as a pre-producer amendment. `EVENT_CONTRACTS.md:263` explicitly calls out that this would normally require v2, records why it does not here, and states the exception closes once the first producer ships. I do not think this needs v2.

Checks run: `validate_examples.mjs`, `consistency_check.mjs`, `safety_invariants.mjs`, and `ac_traceability.mjs` all passed.

Verdict: ACCEPT_WITH_CONDITIONS.
