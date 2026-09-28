# Codex - how ADR-0021 should be accepted (process question)

> Raw output, transcribed verbatim from the `codex exec` run of 2026-09-28. Read-only.
> Recommendation: PATH 3.

(1) Yes, PATH 2 as stated crosses the line if it means adding `Confluent.Kafka`, `Testcontainers.Kafka`, package references, lock files, or compose/test manifests to this repo before the product owner requests Phase 3. In this repository’s own language, that is not neutral ADR work: it is dependency installation, executable manifest work, and Phase 3 scaffolding. The gate says Phase 3 is not requested, and “authorized is not the same as started.”

A narrower subset of PATH 2 is still permitted: read-only or documentation-only fact checking. For example, inspecting NuGet/Docker metadata, reading package licence artifacts without adding them to a repo project, or recording a Docker image digest in docs can be ADR evidence. But creating a project reference and restoring the repository graph is Phase 3 startup work.

(2) ADR-0020 did not fully verify everything before acceptance. It was accepted in commit `31debf3` as documentation/decision work. The first Phase 2 implementation commit came after that (`66aee02`) and added `.csproj` files and protocol code. ADR-0020 explicitly carried “Open items for the Phase 2 spike,” including on-wire Modbus behavior, OPC UA write callback behavior, snapshot consistency, read-only enforcement, image digests, transitive dependency versions, and full licence scan. Later Phase 2 commits, especially the lockfile/licence-scan work in `b8fc7e0`, resolved those.

That precedent implies ADR-0021 may be accepted as the architecture decision while carrying implementation-verification conditions into the Phase 3 spike, provided the conditions are explicit, testable, and treated as gates before Phase 3 can be closed.

(3) Honest split of ADR-0021’s eight conditions:

Can verify before Phase 3, without starting it:
- Condition 1, partially: packaged `librdkafka.redist` licence files can be inspected as external/package evidence. Extending repo scanner behavior may be docs/tooling only if no dependency graph is added, but proving the actual restored repo entry waits.
- Condition 2, partially: NuGet metadata can confirm `net10.0` compatibility/resolution claims. “Run on net10.0” cannot be fully proven without a package-consuming executable/test.
- Condition 3, partially: `apache/kafka:4.3.1` digest can be pulled/recorded in docs. Asserting Testcontainers uses it cannot be proven until test code exists.
- Condition 7, partially: the required consumer group protocol/settings can be documented before Phase 3. Runtime proof waits.
- Condition 8, partially: CI/test policy can be documented before Phase 3. Actual fail-loud behavior waits for tests.

Cannot honestly verify without starting Phase 3:
- Condition 4: proving bootstrap describes first and never mutates partitions requires the Phase 3 bootstrap implementation/tests.
- Condition 5: a full restored graph scan requires adding/restoring the new package graph in a real project/lockfile.
- Condition 6: semantic smoke tests require Kafka client code, broker runtime, and tests.
- The executable parts of 2, 3, 7, and 8 also wait for Phase 3.

(4) Better third path: accept ADR-0021 now, but rewrite the “Acceptance conditions” framing into two buckets: “pre-acceptance evidence” and “Phase 3 entry/spike verification conditions.” Do the documentation-only evidence now, record that no package references, lock files, compose manifests, or test projects may be added until the product owner requests Phase 3, and make Phase 3 stop immediately if any spike condition invalidates the ADR.

(5) I recommend PATH 3. PATH 1 is close, but too loose if it simply carries all eight forward without separating what can already be checked. PATH 2 is too aggressive because its useful caution comes bundled with repo-level dependency/manifest work that this project treats as starting the phase.

The strongest argument against PATH 3 is that accepting ADR-0021 before a restored licence graph and runtime smoke test may force an ADR amendment if `librdkafka.redist`, Docker image behavior, or `net10.0` execution reveals a blocker. That is a real cost, but ADR-0020 already chose that trade-off: accept the decision, then verify implementation-sensitive facts during the requested phase spike.

RECOMMENDATION: PATH 3 - accept the ADR decision now after docs-only evidence, explicitly carry the unverifiable items as Phase 3 spike/entry conditions, and do not add dependencies or manifests until Phase 3 is requested.
