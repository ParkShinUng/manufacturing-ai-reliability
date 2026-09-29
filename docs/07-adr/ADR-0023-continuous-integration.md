# ADR-0023 — a minimal continuous-integration workflow
Status: **Accepted** (2026-09-29). The product owner delegated this decision to Claude and Codex,
and both concluded independently that it should be added now
([`reviews/phase-3/CODEX_CI_DECISION_raw.md`](../../reviews/phase-3/CODEX_CI_DECISION_raw.md)).
`major infrastructure` puts it under `NFR-012`, so it has an ADR even though it is small.

## Context

**The repository has no CI, and several documents said it did.** `TEST_SPECIFICATIONS.md` says CI
fails if an acceptance criterion is unmapped; `TOOLCHAIN.md` lists a "local and CI broker";
`IMPLEMENTATION_PLAN.md` says Docker-dependent suites run in CI; `ADR-0021` makes that last one an
entry condition (B8). None of it was true. That is a documentation defect whatever is decided, and
no phase owned creating CI — Phase 10 is Kubernetes deployment, a different thing.

Two ways to make the documents true: add CI, or rewrite them to describe only local checks. The
second leaves B8 permanently unmet and every claim in this repository resting on "it passed on the
author's machine", which is exactly the kind of evidence `NFR-005` refuses for performance figures.

## Decision

`.github/workflows/ci.yml`, doing only what the documents already claim:

| Job | Runs | Why |
|---|---|---|
| `contracts` | the four `tests/contract/*.mjs` checks | examples against schemas, cross-document consistency, safety invariants, AC traceability — the last is what `TEST_SPECIFICATIONS.md` §7 said CI enforced |
| `dotnet` | `dotnet restore --locked-mode` · `node scripts/license-scan.mjs` · `dotnet build` · `dotnet test src/dotnet/Mair.sln` | the lock files are honoured, not regenerated; the licence gate runs on the graph that was actually restored; every suite runs |

**Docker is required in the `dotnet` job, and its absence fails the job.** `EventBackbone.Tests`
starts the pinned Kafka image through Testcontainers. A step checks the daemon first and fails with
a plain message if it is missing, so the Kafka suites can never become a silent skip — which is B8.

Triggers: pull requests, pushes to `main`, and manual dispatch. Permissions: `contents: read`, and
nothing else — the workflow deploys nothing, publishes nothing, and reads no secrets.

**Pinned the way everything else is pinned.** Actions by **commit SHA**, not tag — a tag is mutable,
and this repository already treats a floating reference as the same defect as an unpinned
transitive package. The runner image is `ubuntu-24.04`, not `-latest`. The SDK comes from
`global.json`, and Node from a new `.nvmrc` — which `TOOLCHAIN.md` said would be created "at
repository initialisation" and never was.

## What this does not decide

- **Long-running suites stay opt-in.** `LOAD-001` is gated by `MAIR_LOAD_TEST` because it runs for
  an hour; that gate is for duration, not for skipping correctness, and CI does not set it.
- No release, container publishing or deployment automation. Those belong to Phase 10 and need
  their own decisions.

## Consequence for ADR-0021 B8

B8 closes on the **first green run on GitHub**, not on the commit that adds this file. Until a run
has been observed, the workflow is a claim; the run is the evidence.

## Sources

- `actions/checkout` v7.0.1 — `3d3c42e5aac5ba805825da76410c181273ba90b1`
- `actions/setup-dotnet` v6.0.0 — `a98b56852c35b8e3190ac28c8c2271da59106c68`
- `actions/setup-node` v7.0.0 — `820762786026740c76f36085b0efc47a31fe5020`
