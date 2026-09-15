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
