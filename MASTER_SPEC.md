# Manufacturing AI Reliability Platform

> Version: 0.3.0 — APPROVED_WITH_CONDITIONS, all conditions satisfied (2026-09-15)  
> Status: **APPROVED / gate OPEN / Phase 1 authorized / implementation NOT started**  
> Purpose: AI-agent-neutral implementation specification for Codex, Claude Code, or a human engineering team.

## 1. Product statement
Manufacturing AI Reliability Platform is a **Digital Twin/HIL-based manufacturing equipment real-time anomaly prediction, AI safety operation, and MLOps platform**.

The product demonstrates one central engineering principle:

> AI may improve industrial operation, but AI must never become a new single point of failure.

It simulates industrial equipment, ingests telemetry through industrial protocols, streams normalized events, performs anomaly/failure/RUL inference, validates AI recommendations with a deterministic safety supervisor, and preserves safe equipment operation when AI, network, or messaging components fail.

## 2. Portfolio goal
The project is designed to prove senior-level capability across:
- real-time manufacturing software and OT integration;
- distributed event-driven systems;
- production AI serving and MLOps;
- reliability/failure recovery;
- observability and measurable SLOs;
- architecture decision-making and documentation discipline.

This is **not** a functional-safety-certified system and must not claim IEC 61508/SIL certification. `Safety Supervisor` means an application-level deterministic guardrail and fallback layer.

## 3. Architecture principles
1. **Document-first, code-second.** No behavior may be introduced only in code.
2. **AI is advisory, not authoritative.** AI never writes directly to equipment.
3. **Control survives AI failure.** The safe control path works when AI is unavailable.
4. **OT and AI/Data paths are separated.** Streaming/analytics failure cannot block local control.
5. **Event contracts are versioned.** Compatibility is explicit.
6. **Idempotency over wishful exactly-once.** Commands and events carry stable IDs and deduplication rules.
7. **Failure is a first-class feature.** Every major component has defined degraded behavior.
8. **Measure, do not claim.** Portfolio performance numbers are published only after reproducible tests.
9. **Technology must have a documented reason.** Every non-trivial technology choice requires an ADR.
10. **Prefer bounded services over microservice proliferation.** Split only where failure isolation, scaling, runtime, or ownership justifies it.

## 4. System scope
### In scope
- C#/.NET equipment Digital Twin/HIL simulator
- OPC UA + Modbus TCP connectivity
- edge gateway with normalization, buffering, quality tagging, reconnect
- Kafka-based telemetry/event backbone
- anomaly detection + failure/RUL inference
- AI Safety Supervisor with OOD/confidence/data-quality/freshness/rule gates
- deterministic safe fallback controller
- MLflow-based model lifecycle
- Docker/Kubernetes deployment
- Prometheus/Grafana/OpenTelemetry observability
- Next.js operations dashboard
- failure injection, load testing, recovery demonstrations

### Explicitly deferred
- LLM/RAG operations copilot
- Spark/Airflow/Iceberg unless a documented requirement makes them necessary
- ROS2/robotics
- 3D Digital Twin
- certified functional safety
- hard real-time control guarantees

## 5. Reference logical architecture
```text
┌──────────────────────────────────┐
│ Digital Twin / HIL Simulator     │  C#/.NET
│ equipment states + fault inject  │
└───────────────┬──────────────────┘
                │ OPC UA / Modbus TCP
                v
┌──────────────────────────────────┐
│ Edge Gateway                     │  C#/.NET on Linux
│ normalize / quality / buffer     │
│ reconnect / local safe mode      │
└───────────────┬──────────────────┘
                │ normalized events
                v
             Kafka
       ┌────────┼───────────────┐
       │        │               │
       v        v               v
 telemetry   AI feature      observability
 storage     + inference     pipeline
                │
                v
┌──────────────────────────────────┐
│ AI Prediction Service            │ Python
│ anomaly / failure / RUL          │
└───────────────┬──────────────────┘
                │ recommendation event
                v
┌──────────────────────────────────┐
│ AI Safety Supervisor             │ C#/.NET
│ OOD/confidence/quality/freshness │
│ rules + limits + fallback        │
└───────────────┬──────────────────┘
                │ accepted bounded command
                v
┌──────────────────────────────────┐
│ Control Service                  │ C#/.NET
│ idempotent command execution     │
└───────────────┬──────────────────┘
                │ OPC UA / Modbus TCP
                v
             Equipment
```

## 6. Runtime boundaries
| Component | Runtime | Why |
|---|---|---|
| Simulator | C#/.NET | Mirrors industrial-control expertise and strong concurrency tooling |
| Edge Gateway | C#/.NET/Linux | OT connectivity, long-running reliability, predictable memory/lifecycle |
| Kafka | JVM/managed infra | Durable replayable event backbone and consumer isolation |
| AI Training/Inference | Python | ML ecosystem and model experimentation |
| Safety Supervisor | C#/.NET | Deterministic rules close to control domain; independent from Python AI process |
| Control Service | C#/.NET | Clear command ownership, idempotency, equipment protocol integration |
| Dashboard | Next.js/TypeScript | Operator-facing web experience and portfolio visibility |
| MLflow | Python ecosystem | Experiment/model registry lifecycle |
| Observability | Prometheus/Grafana/OTel | Metrics, traces, logs, measurable failure behavior |

## 7. AI safety policy
AI recommendations are **advisory only** and are accepted only when **every** gate in the canonical
safety gate table passes.

> **The canonical, normative gate list lives in `docs/04-ai/AI_SAFETY_AND_MLOPS.md` §2.**
> v0.2 carried a second, differently-ordered copy here (9 gates vs 10, in conflicting order, while
> `REASON_CODES.md` implemented the union of both). Two normative copies of one list was the root
> cause, so the duplicate has been **removed** rather than re-synchronised (DEC-005, GAP-025).

Invariants that hold regardless of the gate table:
1. **Any** failed gate rejects the recommendation. There is no partial-trust or reduced-authority
   acceptance path; OOD in particular is a **hard reject**.
2. Every accepted and rejected recommendation produces an auditable decision record with stable
   reason codes (FR-032).
3. AI rejection is an **expected system state**, not an exception or a crash.
4. The Safety Supervisor can never raise its own authority; the Control Service grants it.

## 8. Primary portfolio demonstrations
1. Gradual bearing degradation is injected; AI detects anomaly and predicts failure/RUL.
2. OOD sensor pattern is injected; AI recommendation is rejected and fallback remains active.
3. Inference service is killed; control remains available without direct AI dependency.
4. Kafka is restarted; replay/recovery behavior is measured and no undocumented loss is claimed.
5. A bad model candidate is canary deployed; quality/fallback metrics degrade and rollback is demonstrated.

## 9. Source-of-truth order
When documents conflict, use this precedence:
1. `MASTER_SPEC.md`
2. approved requirement documents (`docs/01-requirements`)
3. accepted ADRs (`docs/07-adr`)
4. **machine-readable contracts in `contracts/`** (JSON Schema, Protobuf, OpenAPI)
5. contract prose (`docs/03-contracts`)
6. component/AI/operations specs
7. development/test specifications
8. implementation code

**Machine-readable contracts outrank contract prose (DEC-007).** v0.2 was ambiguous here:
`REPOSITORY_STRUCTURE.md` called `contracts/` the machine-readable source of truth while this section
ranked `docs/03-contracts` above it — and the two had already diverged (the telemetry example in
prose omitted three schema-required fields). Prose documents are **explanatory**; every example they
contain must be a validated instance of the corresponding schema, enforced by a contract test.

Code never overrides documentation. Conflicts require a documentation change first.

## 10. Implementation gate status

**Specification version: v0.3.**
**Human decision: APPROVED_WITH_CONDITIONS — all conditions satisfied (2026-09-15).**
**Gate: OPEN. Phase 1: IN PROGRESS — equipment simulator core implemented 2026-09-15
(`src/dotnet/EquipmentSimulator/`, AC-018/019/020, PROP-03). Phase 2 is not authorized.**

The dual-agent engineering review (Claude Code + Codex) completed with 0 unresolved P0 and P1.
The product owner recorded:

| ID | Decision | Outcome |
|---|---|---|
| HD-001 | Max authorization staleness | **APPROVED** — 60 s |
| HD-002 | Manual/operator command origin | **APPROVED** — keep removed |
| HD-003 | ADR-0011 control authority change | **APPROVED** — ratified |
| HD-004 | Overall v0.3 approval | **APPROVED_WITH_CONDITIONS** |

Conditions C-1, C-2 and C-3 are the three decisions above; all are satisfied, with evidence recorded
in `docs/10-human-review/v0.3/HUMAN_APPROVAL.md`.

**Phase 1 is authorized.** It is **not** started, and does not start automatically — the approval
removes a prohibition; it does not issue a task. Implementation begins when the product owner asks
for it.

Two things approval does **not** change:

- every performance figure remains `TARGET (unmeasured)` until a report exists under `reports/`
  (NFR-011, AC-042);
- AC-011 and AC-012, which prove the headline reliability claim, cannot run until **Phase 7**
  delivers the Control Service watchdog. ADR-0011 is ratified but unproven until then.

Every change from here still follows the documentation-first workflow
(`DEFINITION_OF_READY.md`, `DUAL_AGENT_PROTOCOL.md`).

## 11. Document map
See `README.md` for the required reading order. AI coding agents must also obey `AGENTS.md` or `CLAUDE.md`.
