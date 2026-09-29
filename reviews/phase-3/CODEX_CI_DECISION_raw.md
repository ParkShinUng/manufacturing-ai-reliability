# Codex - should the repository get CI now?

> Raw output, transcribed verbatim from the `codex exec` run of 2026-09-29. Read-only. The product
> owner delegated this decision to Claude and Codex. Decision: ADD_CI_NOW.

Yes. The documents that speak in present tense about CI are false today where they assert enforcement by CI. That is a documentation defect regardless of the final decision, because the repo currently has no CI surface to perform those gates. `TEST_SPECIFICATIONS.md` is partly self-aware, but `TOOLCHAIN.md`, `IMPLEMENTATION_PLAN.md`, and ADR-0021 B8 still describe CI behavior that does not exist.

Adding CI now is in scope only if treated as closing a Phase 3 entry-condition defect, not as casual tooling cleanup. ADR-0021 B8 explicitly requires Docker-dependent Kafka tests to run in CI and fail rather than skip, and Phase 3 is already in progress under that ADR. But because CI is new infrastructure under NFR-012, it needs its own small ADR or ADR amendment: small because the decision is narrow, development-only, and does not change product runtime architecture; still an ADR because it introduces an infrastructure component and external execution environment.

The minimum CI that makes the documents true is:

- triggers: `pull_request`, `push` to the protected/default branch, and `workflow_dispatch`;
- contract/docs job: run all `tests/contract/*.mjs`, including `ac_traceability.mjs`;
- license job: run `node scripts/license-scan.mjs`;
- .NET job: run `dotnet test src/dotnet/Mair.sln`;
- Docker must be available in that .NET job, and `EventBackbone.Tests` must run there through Testcontainers against the pinned Kafka image. The Docker-dependent suite must not be excluded or converted to a skip-on-CI path. If Docker is missing, the job should fail.

The cost of not adding CI now is that ADR-0021 B8 remains PARTIAL, Phase 3 keeps carrying an unmet entry condition, and the repo continues making unenforced claims about CI. To defer honestly, the documents must be amended: remove or future-tense the CI claims in `IMPLEMENTATION_PLAN.md`, change ADR-0021 B8 to a deferred/non-entry condition or local-only condition, replace “CI fails” language in `TEST_SPECIFICATIONS.md` with the actual local checks, and clarify `TOOLCHAIN.md`’s “local and CI broker” wording.

My recommendation is to add CI now, after a small ADR. The strongest argument against it is scope control: CI was not assigned to a roadmap phase, and adding hosted automation can bring noisy runner/Docker/Kafka failures into a Phase 3 that is already about Kafka semantics, not project operations.

DECISION: ADD_CI_NOW - add a minimal CI workflow under a small NFR-012 ADR so ADR-0021 B8 becomes true instead of papered over.
