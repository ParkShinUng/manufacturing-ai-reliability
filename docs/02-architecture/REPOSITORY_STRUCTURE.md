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
│  │  ├─ EquipmentSimulator/
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

## Shared code rules
- `.NET BuildingBlocks` may contain only cross-cutting technical primitives (correlation, result types, observability helpers), not domain business rules.
- Python `mair_ml_core` owns feature definitions and preprocessing shared by training and inference to prevent training-serving skew.
- Safety rules are owned only by `SafetySupervisor` and its tests/configuration.
