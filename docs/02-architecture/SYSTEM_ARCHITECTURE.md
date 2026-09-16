# System Architecture

> v0.3: adds `mlops-publisher`, states the three-layer authority model, and corrects two statements
> that v0.2 left behind — control **mode ownership** moved from the Safety Supervisor to the Control
> Service (ADR-0011), and the decision topic is `factory.safety-decisions.v1`, not `safety.decision`.

## 1. Authority model (ADR-0011)

Read this before the component list; it is what the boundaries exist to enforce.

| Layer | Owner | Authority | Survives |
|---|---|---|---|
| **L1** advisory | `safety-supervisor` | propose a bounded setpoint | — |
| **L2** application control | **`control-service`** — sole *application* equipment writer | apply, clamp, fence, own control mode, watchdog-fallback | Supervisor death |
| **L3** equipment self-protection | `equipment-simulator` | protective trips, dead-man revert | **total platform death** |

FR-035 constrains **application** components. L3 is the equipment protecting itself, in the same
class as an over-temperature trip.

## 2. Bounded components

### 1. `equipment-simulator`
C#/.NET. Owns equipment physics, state, fault injection, and **L3 self-protection**. Implements the
OPC UA and Modbus TCP server endpoints; never bypasses the gateway.
Design: [`11-service-design/EQUIPMENT_SIMULATOR.md`](../11-service-design/EQUIPMENT_SIMULATOR.md)

### 2. `edge-gateway`
C#/.NET Worker on Linux. Owns OPC UA/Modbus connectivity, normalisation, timestamps, **canonical
quality flags and derived `quality.overall`** (ADR-0018), reconnect, bounded local buffering, and
Kafka production. Publishes gateway-observed state to `factory.equipment-states.v1`.
**Read-only toward equipment, enforced by the server — by session provisioning on OPC UA, and by
connecting only to the read-only Modbus listener `5020`, which has no write code path (OD-003).**
Design: [`EDGE_GATEWAY.md`](../11-service-design/EDGE_GATEWAY.md)

### 3. `prediction-service`
Python. Consumes telemetry, builds feature windows, loads an authorized model, emits predictions
carrying `deploymentStage` and `featureSchemaVersion`. **No equipment credentials.** Emits **no
prediction at all** when the feature window is insufficient.
Design: [`PREDICTION_SERVICE.md`](../11-service-design/PREDICTION_SERVICE.md)

### 4. `safety-supervisor`
C#/.NET. Consumes predictions, equipment states, and model authorizations; evaluates the 13 canonical
safety gates; emits `factory.safety-decisions.v1` and, when accepted, a bounded command intent.
Owns **gate logic and reason codes**.
**Does not own control mode** — the Control Service does, because mode must be owned by the component
that still exists when the Supervisor dies.
Design: [`SAFETY_SUPERVISOR.md`](../11-service-design/SAFETY_SUPERVISOR.md)

### 5. `control-service`
C#/.NET. **Sole application owner of equipment writes.** Authenticates command origin from verified
mTLS identity, enforces `controlEpoch` fencing, expiry, supersession, final bounds, rate budget, and
idempotency. **Owns control mode and the fallback watchdog.**
Design: [`CONTROL_SERVICE.md`](../11-service-design/CONTROL_SERVICE.md)

### 6. `training`
Python batch jobs. Build datasets and model artifacts, publish to MLflow. Not part of the control
runtime. Shares feature code with serving via `mair_ml_core`.
Design: [`AI_TRAINING_PIPELINE.md`](../11-service-design/AI_TRAINING_PIPELINE.md)

### 7. `mlops-publisher` — **new in v0.3 (ADR-0019)**
Translates MLflow registry transitions into `ModelAuthorization` events on the compacted topic
`factory.model-deployments.v1`, and emits an `AuthorizationWatermark` every 30 s.
It exists to **remove MLflow from the safety path**: the Supervisor consumes authorization from Kafka
and never calls the registry on a decision. Its own death is detectable within 90 s and fails closed.
Justified under NFR-012 in [`MODEL_LIFECYCLE.md`](../11-service-design/MODEL_LIFECYCLE.md).

### 8. `operations-service`
ASP.NET Core. Read APIs plus background projections from Kafka into PostgreSQL. **Not in the command
path.** Demo-only fault injection is routed through guarded endpoints to simulator admin APIs.
Design: [`OPERATIONS_API.md`](../11-service-design/OPERATIONS_API.md)

### 9. `operations-dashboard`
Next.js/TypeScript. Reads the Operations Service only. **Holds no equipment-write credentials, and no
Operations API route reaches the Control Service** — the absence is architectural, not merely hidden.
Design: [`OPERATIONS_DASHBOARD.md`](../11-service-design/OPERATIONS_DASHBOARD.md)

Supporting components with their own designs: the
[Kafka event backbone](../11-service-design/KAFKA_EVENT_BACKBONE.md),
[operational data store](../11-service-design/OPERATIONAL_DATA.md), and
[observability platform](../11-service-design/OBSERVABILITY_PLATFORM.md).

## 3. Important separation

Local safe-control capability must not depend synchronously on Kafka, MLflow, the dashboard, or
training. AI recommendations arrive asynchronously; when unavailable or stale, fallback policy
applies — and when the *Supervisor itself* is unavailable, the L2 watchdog and then the L3 dead-man
apply (ADR-0011). Each of these is a labelled row in
[`FAILURE_MODEL.md`](FAILURE_MODEL.md) so the claim is falsifiable rather than asserted.

## 4. Data path vs command path

- **Data path**: equipment → gateway → Kafka → consumers / storage / AI.
- **AI recommendation path**: prediction → Kafka → Safety Supervisor.
- **Authorization path**: MLflow → `mlops-publisher` → Kafka (compacted) → Safety Supervisor.
- **Command path**: Safety Supervisor → **gRPC/mTLS** → Control Service → equipment.
  Kafka is **not** on this path (ADR-0009); outcomes are published to it asynchronously for audit.

This separation is what prevents AI or data-platform instability from becoming equipment write
authority.

## 5. Canonical topics

`factory.telemetry.v1` · `factory.predictions.v1` · `factory.safety-decisions.v1` ·
`factory.control-outcomes.v1` · `factory.equipment-states.v1` · `factory.model-deployments.v1` ·
`factory.faults.v1`

Full topology, partitions, retention, DLQ, and delivery semantics:
[`03-contracts/KAFKA_TOPOLOGY_AND_SEMANTICS.md`](../03-contracts/KAFKA_TOPOLOGY_AND_SEMANTICS.md).
