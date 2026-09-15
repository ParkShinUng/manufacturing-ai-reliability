# Baseline Decisions — Resolved for v0.2

The following baseline choices are now normative unless changed through the documentation-first workflow.

1. **Equipment model**: variable-speed motor-driven conveyor/drive unit with bearing degradation model.
2. **Demo scale**: 20 equipment instances at 100 ms telemetry. Load profile is configurable up to 250 virtual equipment instances; any published throughput claim must be measured.
3. **Telemetry cadence**: raw telemetry every 100 ms per equipment.
4. **Feature/inference cadence**: maintain a 60-second feature window, produce 1-second aggregate features, run inference every 5 seconds per equipment.
5. **OOD baseline**: training-distribution z-score envelope + hard feature validity/range checks. More advanced OOD methods require evidence and an ADR.
6. **Operational persistence**: PostgreSQL stores 1-second aggregates, predictions, safety decisions, command results, fault-injection history, and model-deployment metadata. Raw 100 ms telemetry is not duplicated into PostgreSQL in baseline; Kafka provides short-term replay retention.
7. **Kafka local topology**: one KRaft broker for local development. Multi-broker HA is not a baseline claim; it may be added only for a documented HA experiment.
8. **Model rollout**: candidate models first run in shadow mode on the same telemetry with a separate consumer group/topic. If approved, AI authority can be canaried by an explicit simulator-equipment cohort. Candidate output never gains control authority by network percentage alone.
9. **Command transport**: Safety Supervisor → Control Service uses gRPC, not Kafka. Kafka receives audit/outcome events but is not a synchronous dependency of command execution.
10. **Manual / operator-originated control: DEFERRED.** v0.2 allowed "explicit human/manual mode"
    to originate a production command. That allowance is **removed** in v0.3 (DEC-004): it was a
    second command origin with no state, authorization, interlock, or relationship to the Safety
    Supervisor, and therefore a documented bypass of the deterministic safety path. In the v0.3
    baseline the Safety Supervisor is the only production command origin. Manual operator control
    has **no contract surface** in v0.3 and may be introduced later only through the normal
    documentation-first workflow with its own ADR, state machine, authorization model, and
    acceptance criteria. Whether operators should ever command equipment directly is a **product**
    question carried to human review as HD-002.

11. **Default simulator control profile**: nominal operation rate 100%, fallback rate 60%, AI-authorized range 60–100%, max rate change per accepted recommendation 10 percentage points, telemetry freshness 2 seconds, prediction TTL 10 seconds. These are simulator demonstration values, not real industrial safety limits.
