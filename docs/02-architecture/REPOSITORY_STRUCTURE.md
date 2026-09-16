# Repository Structure

The implementation must use this top-level layout unless changed by ADR before code changes.

```text
manufacturing-ai-reliability/
├─ MASTER_SPEC.md
├─ AGENTS.md
├─ CLAUDE.md
├─ README.md
├─ docs/
│  ├─ 00-product/ .. 08-roadmap/     (requirements, architecture, contracts, AI, ops, dev, ADR, roadmap)
│  ├─ 09-decisions/                  # Decision Records: how each decision was CHALLENGED (v0.3)
│  ├─ 10-human-review/<version>/     # human review packet + approval record (v0.3)
│  └─ 11-service-design/             # 12 implementation-ready service designs (v0.3)
├─ reviews/<version>/                # raw dual-agent review transcripts (v0.3)
├─ contracts/
│  ├─ jsonschema/
│  │  └─ v1/
│  ├─ proto/
│  │  └─ control/v1/
│  └─ openapi/
├─ src/
│  ├─ dotnet/
│  │  ├─ Mair.sln
│  │  ├─ BuildingBlocks/
│  │  ├─ EquipmentSimulator/          # L3 domain core - MUST stay dependency-free (see below)
│  │  ├─ EquipmentSimulator.Protocols/ # OPC UA + Modbus servers; where the OT libraries live
│  │  ├─ EquipmentSimulator.Tests/    # unit tests live alongside source (TEST_SPECIFICATIONS.md 1)
│  │  ├─ EdgeGateway/
│  │  ├─ SafetySupervisor/
│  │  ├─ ControlService/
│  │  └─ OperationsService/
│  ├─ python/
│  │  ├─ mair_ml_core/
│  │  ├─ prediction_service/
│  │  └─ training/
│  └─ web/
│     └─ operations-dashboard/
├─ deploy/
│  ├─ compose/
│  ├─ kubernetes/
│  └─ helm/
├─ observability/
│  ├─ prometheus/
│  ├─ grafana/
│  └─ otel/
├─ tests/
│  ├─ contract/                      # runnable NOW, dependency-free, no app code required
│  ├─ integration/
│  ├─ failure/
│  ├─ load/
│  └─ e2e/
├─ global.json                        # pins the .NET SDK feature band (TOOLCHAIN.md)
├─ scripts/
└─ reports/
   ├─ load/
   └─ failure/
```

## Documentation directories added in v0.3

| Directory | Purpose |
|---|---|
| `docs/09-decisions/` | Decision Records — the adversarial exchange behind each decision. An ADR states *what* was decided; a DEC shows *how it was challenged*. |
| `docs/10-human-review/<version>/` | Human review packet. `HUMAN_APPROVAL.md` here is the implementation gate. |
| `docs/11-service-design/` | One implementation-ready design per service. |
| `reviews/<version>/` | Raw, unedited dual-agent transcripts. Kept because a summary written by one party to a disagreement is not evidence. |
| `tests/contract/` | Dependency-free verification runnable during the specification phase. |

## Contract ownership
**Machine-readable contracts in `contracts/` are authoritative over contract prose in
`docs/03-contracts/`** (ADR-0017). Prose is explanatory; every example it contains must be a
validated instance, enforced by `tests/contract/validate_examples.mjs`.

`contracts/` contains machine-readable source-of-truth schemas. Language DTO/models are generated from or validated against these contracts. Hand-edited DTO divergence is prohibited.

## Why the simulator is two projects (added 2026-09-16, Phase 2)

`EquipmentSimulator` holds the **L3 domain core** — physics, state machine, protective trips, the
dead-man revert. It references **nothing outside the framework**, and `AC-020` asserts that as a
test: "with the entire platform stopped" is only a real claim if no component's absence can disable
the protective trip.

Adding the OPC UA and Modbus libraries to that project would have destroyed the property. So
protocol hosting lives in `EquipmentSimulator.Protocols`, which depends on the core; the core never
depends on it. The dependency arrow points **inward**, toward the layer that must survive.

This was found in Phase 2 when the first `NModbus` package reference went onto the wrong project.
The AC-020 test did not fail immediately — `GetReferencedAssemblies()` reports only assemblies the
compiler actually emitted a reference to, so a package sitting unused in the graph is invisible to
it. It would have failed on the first line of protocol code. Worth knowing: that test proves the
property only for code that exists, which is why the **project boundary** carries the guarantee and
the test confirms it.

## Shared code rules
- `.NET BuildingBlocks` may contain only cross-cutting technical primitives (correlation, result types, observability helpers), not domain business rules.
- Python `mair_ml_core` owns feature definitions and preprocessing shared by training and inference to prevent training-serving skew.
- Safety rules are owned only by `SafetySupervisor` and its tests/configuration.
