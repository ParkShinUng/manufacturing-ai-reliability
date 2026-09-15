# Event Contracts

> **This document is explanatory. The authoritative contracts are the JSON Schemas in
> `contracts/jsonschema/v1/` (DEC-007, ADR-0017, `MASTER_SPEC.md` §9).**
> Every example below is a validated instance of its schema, enforced by the contract test
> `tests/contract/validate_examples.mjs`. In v0.2 the telemetry example here omitted three
> schema-required fields and would have failed its own contract (GAP-001); the contract test exists
> so that cannot recur.

All messages use JSON in the baseline (ADR-0006). Topic topology, partitions, retention, DLQ, and
delivery semantics are in `KAFKA_TOPOLOGY_AND_SEMANTICS.md`.

## 1. Common envelope (DEC-007)

Every event carries: `eventId, eventType, schemaVersion, equipmentId?, occurredAtUtc,
ingestTimeUtc, correlationId, causationId, producer`.

`sequence` is **deliberately not** in the envelope. It appears only where a consumer genuinely needs
gap or order detection, each with an explicit scope:

| Field | Topic | Scope | Assigned by | Why there |
|---|---|---|---|---|
| `sequence` | `factory.telemetry.v1` | per `equipmentId` | **the equipment** | a gateway-assigned sequence would stay continuous even when the gateway dropped samples, making loss undetectable |
| `stateSequence` | `factory.equipment-states.v1` | per `equipmentId` | **the gateway** | distinctly named so it can never be confused with telemetry `sequence` |

`producer` is retained in the envelope because the `causationId` chain cannot be audited across
services without knowing which service emitted each link (NFR-002).

## 2. Causation chain

```text
telemetry (causationId = null, originates the chain)
   -> featureWindow.windowId
        -> prediction (causationId = windowId)
             -> safety-decision (causationId = predictionId)
                  -> control-outcome (causationId = decisionId)
```

`correlationId` is constant across the whole chain; `causationId` names the immediate parent. This is
what makes AC-006 a single indexed query rather than a reconstruction.

## 3. Topic summary

| Topic | Schema | Key |
|---|---|---|
| `factory.telemetry.v1` | `telemetry.schema.json` | `equipmentId` |
| `factory.predictions.v1` | `prediction.schema.json` | `equipmentId` |
| `factory.safety-decisions.v1` | `safety-decision.schema.json` | `equipmentId` |
| `factory.control-outcomes.v1` | `control-outcome.schema.json` | `equipmentId` |
| `factory.equipment-states.v1` | `equipment-state.schema.json` | `equipmentId` |
| `factory.model-deployments.v1` | `model-authorization.schema.json` | `modelName` |

## 4. Examples

All examples live in `contracts/examples/` and are validated in CI. They are reproduced here for
readability only — **if this document and the example file ever differ, the file wins.**

### 4.1 Telemetry — healthy sample

Note that all **seven** measurements are present. The v0.2 example carried only four.

```json
{
  "eventId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "eventType": "factory.telemetry",
  "schemaVersion": 1,
  "equipmentId": "eq-001",
  "eventTimeUtc": "2026-09-15T04:00:00.100Z",
  "ingestTimeUtc": "2026-09-15T04:00:00.118Z",
  "occurredAtUtc": "2026-09-15T04:00:00.100Z",
  "sequence": 123456,
  "producer": "edge-gateway@gw-1",
  "sourceProtocol": "OPC_UA",
  "quality": { "overall": "GOOD", "flags": [] },
  "measurements": {
    "temperatureC": 48.2,
    "vibrationRms": 2.9,
    "currentA": 12.1,
    "voltageV": 400.3,
    "rpm": 1780.0,
    "torqueNm": 42.4,
    "operationRatePct": 100.0
  },
  "equipmentState": "RUNNING",
  "correlationId": "9c1f6d2a-7b3e-4a51-8f2d-2c4b6e8a0d11",
  "causationId": null
}
```

### 4.2 Telemetry — dead sensor (DEC-008)

This shape was **impossible to express in v0.2**. The vibration sensor has dropped out: the value is
`null`, a flag names the channel and cause, and `overall` is `BAD` because `vibrationRms` is
safety-required. No value is fabricated.

```json
{
  "eventId": "7b52009b-64fd-4a1f-9b1e-0a2f4c6d8e10",
  "eventType": "factory.telemetry",
  "schemaVersion": 1,
  "equipmentId": "eq-001",
  "eventTimeUtc": "2026-09-15T04:03:11.400Z",
  "ingestTimeUtc": "2026-09-15T04:03:11.421Z",
  "occurredAtUtc": "2026-09-15T04:03:11.400Z",
  "sequence": 125234,
  "producer": "edge-gateway@gw-1",
  "sourceProtocol": "MODBUS_TCP",
  "quality": {
    "overall": "BAD",
    "flags": [
      { "channel": "vibrationRms", "flag": "SENSOR_DISCONNECTED", "detail": "quality bitmap bit 5 clear" },
      { "channel": "__event__", "flag": "TIMESTAMP_SYNTHESISED", "detail": "modbus has no source timestamp" }
    ]
  },
  "measurements": {
    "temperatureC": 51.7,
    "vibrationRms": null,
    "currentA": 12.6,
    "voltageV": 399.8,
    "rpm": 1779.2,
    "torqueNm": 43.1,
    "operationRatePct": 100.0
  },
  "equipmentState": "DEGRADED",
  "correlationId": "1d4e7a90-5c2b-4f88-b3a6-7e9c0f1a2b33",
  "causationId": null
}
```

### 4.3 Prediction

```json
{
  "eventId": "a1c2e3f4-5678-49ab-8cde-f01234567890",
  "eventType": "factory.prediction",
  "schemaVersion": 1,
  "predictionId": "c9b8a7d6-1234-4e5f-9a8b-7c6d5e4f3a21",
  "equipmentId": "eq-001",
  "predictedAtUtc": "2026-09-15T04:00:05.000Z",
  "occurredAtUtc": "2026-09-15T04:00:05.000Z",
  "ingestTimeUtc": "2026-09-15T04:00:05.031Z",
  "producer": "prediction-service@pred-1",
  "model": {
    "name": "bearing-health",
    "version": "1.4.0",
    "runId": "mlflow-run-8a7b6c5d",
    "deploymentStage": "PRODUCTION",
    "featureSchemaVersion": 3
  },
  "featureWindow": {
    "windowId": "0f1e2d3c-4b5a-4698-8877-665544332211",
    "startUtc": "2026-09-15T03:59:05.000Z",
    "endUtc": "2026-09-15T04:00:05.000Z",
    "validSampleRatio": {
      "temperatureC": 1.0, "vibrationRms": 0.99, "currentA": 1.0,
      "rpm": 1.0, "operationRatePct": 1.0, "voltageV": 1.0, "torqueNm": 0.98
    }
  },
  "outputs": {
    "anomalyScore": 0.84,
    "failureProbability": 0.71,
    "rulMinutes": 380,
    "confidence": 0.92,
    "oodScore": 0.12,
    "recommendedOperationRatePct": 70.0
  },
  "correlationId": "9c1f6d2a-7b3e-4a51-8f2d-2c4b6e8a0d11",
  "causationId": "0f1e2d3c-4b5a-4698-8877-665544332211"
}
```

### 4.4 Safety decision — accepted with clamping

The AI requested 70 %, but the baseline was 100 % and the per-decision limit is 10 pp, so the
decision is `ACCEPT` at 90 % carrying `RATE_CHANGE_CLAMPED`. **Both** the requested and bounded
values are recorded, so the clamp is auditable as a modification of AI intent rather than silently
swallowed (GAP-033).

```json
{
  "eventId": "b2c3d4e5-6789-4abc-8def-012345678901",
  "eventType": "factory.safety-decision",
  "schemaVersion": 1,
  "decisionId": "5e4d3c2b-1a09-4876-b5c4-d3e2f1a09876",
  "equipmentId": "eq-001",
  "predictionId": "c9b8a7d6-1234-4e5f-9a8b-7c6d5e4f3a21",
  "sourcePredictionAtUtc": "2026-09-15T04:00:05.000Z",
  "decidedAtUtc": "2026-09-15T04:00:05.180Z",
  "occurredAtUtc": "2026-09-15T04:00:05.180Z",
  "producer": "safety-supervisor@sup-1",
  "decision": "ACCEPT",
  "controlMode": "AI_ASSISTED",
  "gateResults": [
    { "gate": "SUPERVISOR_HEALTHY", "passed": true },
    { "gate": "MODEL_AUTHORIZED", "passed": true },
    { "gate": "PREDICTION_FRESH", "passed": true, "observedValue": 0.18, "threshold": 10 },
    { "gate": "PREDICTION_ORDERED", "passed": true },
    { "gate": "TELEMETRY_FRESH", "passed": true, "observedValue": 0.08, "threshold": 2 },
    { "gate": "TELEMETRY_QUALITY", "passed": true, "observedValue": "GOOD" },
    { "gate": "FEATURE_COMPLETE", "passed": true, "observedValue": 0.99, "threshold": 0.8 },
    { "gate": "OOD", "passed": true, "observedValue": 0.12, "threshold": 0.35 },
    { "gate": "CONFIDENCE", "passed": true, "observedValue": 0.92, "threshold": 0.7 },
    { "gate": "EQUIPMENT_STATE_ELIGIBLE", "passed": true, "observedValue": "RUNNING" },
    { "gate": "RATE_OF_CHANGE", "passed": true, "observedValue": 30, "threshold": 10 },
    { "gate": "RATE_BUDGET", "passed": true, "observedValue": 0, "threshold": 25 },
    { "gate": "OPERATING_BOUNDS", "passed": true, "observedValue": 90, "threshold": 60 }
  ],
  "reasonCodes": ["AI_ACCEPTED", "RATE_CHANGE_CLAMPED"],
  "recommendation": {
    "requestedOperationRatePct": 70.0,
    "boundedOperationRatePct": 90.0,
    "wasClamped": true,
    "baselineOperationRatePct": 100.0,
    "rateBudgetRemainingPp": 15.0
  },
  "correlationId": "9c1f6d2a-7b3e-4a51-8f2d-2c4b6e8a0d11",
  "causationId": "c9b8a7d6-1234-4e5f-9a8b-7c6d5e4f3a21"
}
```

### 4.5 Control outcome — autonomous watchdog fallback (DEC-001)

No `decisionId` and no originating command: the Control Service acted on its own after Supervisor
silence. `autonomous: true` and `authenticatedSource` is the Control Service's own identity.

```json
{
  "eventId": "d4e5f607-8901-4234-a567-89abcdef0123",
  "eventType": "factory.control-outcome",
  "schemaVersion": 1,
  "equipmentId": "eq-001",
  "commandId": "11112222-3333-4444-8555-666677778888",
  "decisionId": null,
  "occurredAtUtc": "2026-09-15T04:02:20.500Z",
  "appliedAtUtc": "2026-09-15T04:02:20.610Z",
  "ingestTimeUtc": "2026-09-15T04:02:20.615Z",
  "producer": "control-service@ctl-1",
  "status": "APPLIED",
  "reasonCode": "SUPERVISOR_SILENT",
  "authenticatedSource": "spiffe://mair/control-service",
  "controlEpoch": 42,
  "requestedTargetPct": null,
  "appliedTargetPct": 60.0,
  "wasClamped": false,
  "resultingMode": "SAFE_FALLBACK",
  "modeTransitionId": "M4",
  "operatorId": null,
  "autonomous": true,
  "correlationId": "7a6b5c4d-3e2f-4109-8877-665544332210",
  "causationId": null
}
```

## 5. Compatibility

- Additive **optional** fields are backward compatible within `v1`.
- Removal, type change, or semantic change requires a new major topic and schema version.
- A field name must never be reused with new semantics.
- `schemaVersion` mismatch on a **major** version is rejected by the consumer, not coerced.
- Enum values may be **appended**; renumbering or removing a value is breaking.
