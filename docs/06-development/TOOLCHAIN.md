# Toolchain Baseline — 2026-09-14 (.NET verified 2026-09-15)

The baseline favors supported/LTS or mature stable releases over preview releases.

- .NET SDK: **10.0.400** in `global.json` with `rollForward: latestPatch`, target framework
  `net10.0`, C# 14. *As of 2026-09-29 the development machine has **10.0.401** and no 10.0.400, and
  builds on it through `latestPatch`, which is what that policy is for; CI installs the version
  `global.json` names.*
- Python: **3.13.15** for conservative ML package compatibility
- Node.js: **24.13.0**, pinned in `.nvmrc` (2026-09-29). This line used to say the patch would be
  pinned "at repository initialization"; it never was, and CI (`ADR-0023`) is what made the gap
  matter.
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
| simulator, Modbus TCP server | `NModbus` | **3.0.83** | MIT |

**`Corvus.Text.Json` ships an analyzer.** It arrives with the validator and flags `System.Text.Json`
calls that have a UTF-8 overload (rule `CTJ001`). Under `TreatWarningsAsErrors` that is a build
error in any project that references it, so a dependency is enforcing style on this repository's
code. Accepted — the rule's advice is sound and cheap — but noted, because a dependency that can
fail the build for reasons unrelated to what it was added for is worth knowing about.

The gateway's Modbus **client** has no package since 2026-09-21: NModbus's client reads block a
thread-pool thread each (OD-006), and ADR-0020 was amended to an in-repository async FC04 reader.

### Phase 3 — pinned by ADR-0021, not yet referenced

Accepted 2026-09-28. **Nothing below is in a project file yet**: Phase 3 has not been requested, and
adding a package reference or a lock file before it is would be starting the phase
(`CLAUDE.md`). The versions are fixed here so that when Phase 3 starts, no one chooses them again.

| Component | Package / image | Version | Licence |
|---|---|---|---|
| Kafka producers and consumers | `Confluent.Kafka` | **2.15.1** | Apache-2.0 (package metadata) |
| — its only dependency | `librdkafka.redist` | **2.15.1** | BSD-2-Clause plus 14 permissive components, read from the packaged `LICENSES.txt` |
| integration tests | `Testcontainers` | **4.15.0** | MIT |
| schema validation in the shared consumer | `Corvus.Text.Json.Validator` | **5.6.1** | Apache-2.0 — ADR-0022, chosen after `NJsonSchema` was measured ignoring `const` and `JsonSchema.Net`'s NuGet binary was found to carry a maintenance-fee agreement |
| local and CI broker | `apache/kafka` | **4.3.1**, digest `sha256:77e3df9054047a88b520d0cc46e16696d3b22022e1d580aeccd2632df6532837` | Apache-2.0 |

Two licence items are **open** and are Phase 3 spike conditions, not claims this table makes:
`librdkafka.redist` declares its licence with the deprecated `licenseUrl` form, so
`scripts/license-scan.mjs` will report it unidentified; and the package ships native binaries that
its own `LICENSES.txt` does not cover — OpenSSL 3, libcurl, zlib, zstd, and the MSVC runtime, the
last redistributable under Microsoft's terms rather than an open licence. See ADR-0021 group B.

`OPCFoundation.NetStandard.Opc.Ua.Core` arrives transitively and is not referenced directly. The
`OPCFoundation.NetStandard.Opc.Ua` **meta package is deliberately not used** — it has no framework
assets of its own and pulls in GDS and complex-type assemblies neither service needs.

### Verification record — 2026-09-18

Both steps this section previously listed as outstanding are **done**.

**Transitive versions are pinned.** `RestorePackagesWithLockFile` is on in
`src/dotnet/Directory.Build.props`, and five `packages.lock.json` files are committed. Without them a
restore on another machine can resolve a different transitive graph — the same class of problem as a
floating container tag.

**Licence scan of the full graph: 47 distinct packages, all permissive** *(2026-09-29; 29 in
Phase 2)*.

*Corrected 2026-09-29.* This line said **50** a few hours earlier. That was the number of
**package@version entries**, not of packages: the Kafka test project had three packages —
`Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`
and `Newtonsoft.Json` — at older versions, pulled in by Testcontainers, while the rest of the
solution used newer ones, so each was counted twice. Adding a project reference unified them and the
count fell to 47 with nothing removed. The scanner still keys on package@version, which is right
for licences — two versions can differ — but the headline number is now the distinct count. Run with `node scripts/license-scan.mjs`, which reads
the `.nuspec` and, where the package ships a licence file, the file itself from the NuGet cache.

| Licence | Packages |
|---|---|
| MIT | the six OPC Foundation packages, `NModbus`, `BitFaster.Caching`, the Microsoft.Extensions set, `Testcontainers` and its `Docker.DotNet.Enhanced`, `SSH.NET` and `SharpZipLib` dependencies |
| Apache-2.0 | the xunit set, `Confluent.Kafka` |
| MS-PL | `Xunit.SkippableFact`, `Validation` |
| BSD-2-Clause, by recorded evidence | `librdkafka.redist` |

**`MS-PL` was in the graph in Phase 2 and this table did not say so.** The Phase 2 entry listed only
MIT and Apache-2.0, because it was written from the direct dependencies rather than re-derived from
the scan output. MS-PL is permissive and OSI-approved, so the conclusion held; the table was simply
not a reading of the evidence. Corrected 2026-09-28.

Two findings worth keeping:

- The **packaged** OPC Foundation `LICENSE.txt` is MIT, not merely the repository's. That is the
  claim that matters when the dependency is consumed from NuGet, and it is a different artefact from
  the one ADR-0020 checked on GitHub.
- `xunit.abstractions 2.0.3` still uses the deprecated `licenseUrl` form, so the scanner cannot
  classify it offline. Fetched and confirmed **Apache-2.0**; it is a test-only dependency.
- `librdkafka.redist 2.15.1` is the same shape. Its packaged `LICENSES.txt` was read from inside the
  `.nupkg`: fifteen sections, all permissive.

**The scan is now a gate** (ADR-0021 B2). It used to print "someone has to look at it" and exit 0,
which is a report nobody is obliged to act on. A licence it cannot classify now **fails** unless
`scripts/license-evidence.json` records what was read and where, and an evidence entry that no
longer matches an unclassifiable package fails too — a stale excuse outliving the thing it excused
is how an exception becomes permanent.

**What `librdkafka.redist` ships beyond that licence file** (ADR-0021 B3). The Windows runtimes
carry binaries `LICENSES.txt` says nothing about: OpenSSL 3, libcurl, zlib, zstd, and Microsoft's
`msvcp140` / `vcruntime140`. The first four are permissive; the MSVC runtime is redistributable
under Microsoft's own terms. **What this project deploys is Linux**, and the Linux runtimes contain
exactly one file each — `librdkafka.so`, with those components statically linked — so no Microsoft
redistributable enters a container image. The Windows payload matters only on a developer machine.

No copyleft and no membership-gated licence anywhere in the graph. ADR-0020's licence claim,
deliberately narrowed to the direct packages at the time, now holds for all of them.

`NModbus` targets `net6.0`/`netstandard`/`net46` rather than `net10.0`; it loads on `net10.0`, but
that it is not being rebuilt against current targets is a maintenance signal worth watching.

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
