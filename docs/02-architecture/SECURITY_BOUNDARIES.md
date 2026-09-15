# Security Boundaries

## Zones
1. **OT simulation zone**: simulator protocol endpoints and control write path.
2. **Edge zone**: gateway + control/safety components.
3. **Data/AI zone**: Kafka, prediction, MLflow, training.
4. **Presentation zone**: dashboard/operations API.

## Rules
- prediction/training services receive no direct equipment-write credentials;
- only Control Service may perform equipment writes;
- simulator fault-injection/admin endpoints are disabled outside demo profile;
- secrets use environment/secret-store mechanisms, never repository literals;
- cloud deployment must use TLS and service-specific credentials;
- all control intents and outcomes are audit logged.

Full OT cybersecurity certification is out of project scope, but trust boundaries must be explicit.

## Authentication and authorization mechanisms (v0.3, GAP-081)

v0.2 stated goals ("service-specific credentials", "TLS", "least privilege") but named no mechanism
for any hop, which is what allowed the self-asserted `source` field to pass for access control.

| Hop | Production-like | Local/demo | Authorization |
|---|---|---|---|
| Safety Supervisor → Control Service (gRPC) | **mTLS**, SPIFFE-style workload identity | compose-private network + per-service token mounted only into the Supervisor | identity → permitted command types → permitted equipment scope |
| Dashboard → Operations API | session cookie / JWT with role claims | same | `viewer` (read) / `operator` (read + `STOP_REQUIRED` reset + demo faults) |
| Operations API → simulator admin | mTLS, demo profile only | private network, demo profile only | endpoint **absent** from the production-like build |
| Gateway → equipment (OPC UA) | `Basic256Sha256`, `SignAndEncrypt` | `None` | **read-only session, enforced by the server** |
| Control Service → equipment | as above | as above | the only identity granted write on the setpoint node/register |
| Services → Kafka | TLS + SASL, per-service principals | private network | per-topic ACLs; no service may write a topic it does not produce |

**A global shared secret is forbidden in every profile.** The local token is per-service, is mounted
only into the Supervisor, and its use is logged as a warning at startup.

### Command origin
`source` has been **removed from the command contract**. Origin is derived server-side from the
verified peer identity and recorded as `authenticatedSource` on the outcome (ADR-0016). This closes
the v0.2 bypass in which any process reaching the gRPC port could claim to be the Supervisor.

### Credential rotation
Overlapping validity window of **300 s**; both old and new credentials are accepted during it.
Rotation is **forbidden while any equipment is in `STOP_REQUIRED`**, so rotation can never induce a
spurious fallback or interfere with a stop.

### Audit record (GAP-085)
Every control intent and outcome is audit-logged with: `equipmentId, commandId, decisionId,
authenticatedSource, controlEpoch, requested and applied target, status, reasonCode, resultingMode,
operatorId (required for M7), occurredAtUtc, correlationId, causationId`.
Stored in PostgreSQL (`control_outcome`) and on `factory.control-outcomes.v1`. Retention: 90 days for
outcomes, 1 year for mode transitions. Audit records are **append-only**; no service holds UPDATE or
DELETE permission on them.

### Secrets
Environment or secret-store only; never repository literals. No secret appears in a log line, a
metric label, or an event payload.
