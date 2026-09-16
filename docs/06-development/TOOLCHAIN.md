# Toolchain Baseline — 2026-09-14 (.NET verified 2026-09-15)

The baseline favors supported/LTS or mature stable releases over preview releases.

- .NET SDK: **10.0.400**, target framework `net10.0`, C# 14 — *verified installed 2026-09-15; the
  previously asserted 10.0.401 was never verified and is corrected here per the procedure below*
- Python: **3.13.15** for conservative ML package compatibility
- Node.js: **24 LTS**; pin exact patch in `.nvmrc`/container at repository initialization
- Apache Kafka: **4.3.1**, KRaft mode
- Kubernetes development target: **1.36.x** stable patch line; do not depend on 1.37-only APIs in baseline
- PostgreSQL: major **18**, exact patch pinned in container manifest at repository initialization
- Container images: pin exact tags/digests in reproducible deployment profiles; avoid `latest`

## OT protocol libraries — pinned by ADR-0020 (accepted 2026-09-16)

| Component | Package | Version | Licence |
|---|---|---|---|
| simulator, OPC UA server | `OPCFoundation.NetStandard.Opc.Ua.Server` | **1.5.378.176** | MIT (OPC Foundation MIT License 1.00) |
| edge gateway, OPC UA client | `OPCFoundation.NetStandard.Opc.Ua.Client` | **1.5.378.176** | MIT |
| both, configuration and certificates | `OPCFoundation.NetStandard.Opc.Ua.Configuration` | **1.5.378.176** | MIT |
| simulator and gateway, Modbus TCP | `NModbus` | **3.0.83** | MIT |

`OPCFoundation.NetStandard.Opc.Ua.Core` arrives transitively and is not referenced directly. The
`OPCFoundation.NetStandard.Opc.Ua` **meta package is deliberately not used** — it has no framework
assets of its own and pulls in GDS and complex-type assemblies neither service needs.

Two verification steps are **outstanding** and must run before the first Phase 2 commit, per the
procedure above: a licence scan of the full transitive graph (only the direct packages have been
checked), and resolution of exact transitive versions into a lockfile. `NModbus` targets
`net6.0`/`netstandard`/`net46` rather than `net10.0`; it loads on `net10.0`, but that it is not being
rebuilt against current targets is a maintenance signal worth watching.

## Verification procedure at Phase 1 initialization

The versions above were recorded on 2026-09-14 and are **asserted, not verified**. Before the first
commit of Phase 1:

1. confirm each version exists and is a supported/LTS line at that date;
2. pin exact patch versions and **container image digests**, never a floating tag;
3. record the verification (date, who, resolved digests) in `reports/`;
4. if a version has moved or been withdrawn, update this document **first**, then proceed.

A version asserted in a document is not evidence that it exists.

## Dependency rule
No agent may silently upgrade a major runtime or infrastructure version. Version changes require documentation update plus compatibility verification; major-version changes require ADR.
