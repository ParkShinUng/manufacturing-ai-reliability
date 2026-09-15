# Functional Requirements

Priority: P0 = required for portfolio baseline, P1 = next increment.

## Equipment and OT
- **FR-001 [P0]** Simulator shall emulate 20 concurrent equipment instances in demo profile and a configurable load profile up to 250 instances with configurable telemetry profiles.
- **FR-002 [P0]** Simulator shall support normal, gradual-degradation, sensor-drift, sensor-noise, communication-loss, and OOD scenarios.
- **FR-003 [P0]** Equipment telemetry shall be available through OPC UA and at least one Modbus TCP mapping.
- **FR-004 [P0]** Edge Gateway shall reconnect after protocol interruption without requiring a process restart.
- **FR-005 [P0]** Edge Gateway shall normalize protocol-specific values into a canonical telemetry event.
- **FR-006 [P0]** Every telemetry event shall carry equipment ID, event timestamp, ingest timestamp, sequence, schema version, and quality flags.

## Event platform
- **FR-010 [P0]** Normalized telemetry shall be published to Kafka using a versioned event contract.
- **FR-011 [P0]** Consumers shall tolerate replay and duplicate delivery according to documented idempotency rules.
- **FR-012 [P0]** Kafka consumer lag shall be observable.

## AI
- **FR-020 [P0]** Platform shall expose anomaly score per equipment.
- **FR-021 [P0]** Platform shall expose **both** failure probability and RUL prediction. (v0.2's "either … if feasible" was untestable while the prediction contract carried both fields; resolved in v0.3, GAP-098.) Both fields are nullable in the contract so a model may legitimately decline to produce one, but the baseline model produces both. Verified by AC-024.
- **FR-022 [P0]** Every prediction shall include model version, prediction timestamp, confidence/quality information, and feature-window identity.
- **FR-023 [P0]** Inference outage shall not disable deterministic fallback control.

## Safety supervisor and control
- **FR-030 [P0]** AI services shall not directly issue equipment protocol writes, and shall hold no equipment credentials. Verified by AC-039.
- **FR-031 [P0]** Safety Supervisor shall evaluate freshness, data quality, OOD, confidence, rule constraints, operational limits, and model status.
- **FR-032 [P0]** Every accepted/rejected AI recommendation shall produce an auditable decision record with reason codes.
- **FR-033 [P0]** Rejected, expired, unavailable, or unhealthy AI recommendations shall result in documented fallback behavior.
- **FR-034 [P0]** Equipment commands shall use idempotency keys and bounded target values.
- **FR-035 [P0]** Control Service shall own equipment command execution; **no other application component may write directly to equipment.** Scope clarified in v0.3 (ADR-0011): this constrains *application* components. Equipment self-protection (protective trips and the dead-man revert) is the equipment acting on itself — layer L3 — and is not an application write. Verified by AC-020, AC-039.
- **FR-036 [P0] (new)** The deterministic control path shall remain able to reach a documented safe state when **any single** platform component fails, including the Safety Supervisor itself. Verified by AC-011, AC-012 (ADR-0011).
- **FR-037 [P0] (new)** Equipment command origin shall be authenticated from a verified transport identity and never accepted from a self-asserted payload field. Verified by AC-014 (ADR-0016).

## MLOps
- **FR-040 [P0]** Training runs shall be traceable to model artifact, parameters, metrics, and dataset identifier in MLflow.
- **FR-041 [P0]** Deployed inference shall expose model version.
- **FR-042 [P1]** Candidate model shall support canary deployment.
- **FR-043 [P1]** Demonstration shall show rollback from a regressed candidate model.

## Operator UI
- **FR-050 [P0]** Dashboard shall show equipment health, anomaly, prediction, control mode, model version, and active rejection/fallback reason.
- **FR-051 [P0]** Dashboard shall expose platform health including inference availability, Kafka lag, error rate, and fallback rate.
- **FR-052 [P0]** Demo mode shall allow explicit fault injection only in simulator environments.
