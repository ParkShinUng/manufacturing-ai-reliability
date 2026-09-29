# ADR-0021 — Kafka client library and local broker runtime
Status: **Accepted** (2026-09-28, by the product owner, dual-agent reviewed)
Challenge: [`reviews/phase-3/CODEX_ADR-0021_CHALLENGE_raw.md`](../../reviews/phase-3/CODEX_ADR-0021_CHALLENGE_raw.md)
— `distributed messaging`, `Kafka semantics` and `major dependency` are all on the mandatory
participation list (`DUAL_AGENT_PROTOCOL.md` §2). Codex returned `ACCEPT_WITH_CONDITIONS` with five
P1 findings, all applied; one of them, the producer retry profile, was a contradiction inside
`KAFKA_TOPOLOGY_AND_SEMANTICS.md` §11 rather than in this ADR, and is corrected there.
How to accept it was itself challenged
([`CODEX_ADR-0021_PATH_raw.md`](../../reviews/phase-3/CODEX_ADR-0021_PATH_raw.md)): the evidence that
could be gathered without touching the repository was gathered first, and the rest is carried as
Phase 3 spike conditions, following `ADR-0020`'s precedent.
Phase: prerequisite for Phase 3 (`IMPLEMENTATION_PLAN.md`) — the last one outstanding.

## Context

`ADR-0001` chose Kafka as the event backbone. It chose an architecture, not a dependency: no library
is named anywhere, and nothing says what a broker is on a developer machine. Phase 3's Definition-of
-Ready check found that gap, Codex confirmed it (`reviews/phase-3/`, `P3-DOR-009`), and it is now the
only condition still blocking Phase 3.

What Phase 3 has to do with the client decides what the client has to support
(`KAFKA_TOPOLOGY_AND_SEMANTICS.md`):

| Need | Where it comes from |
|---|---|
| **Idempotent producer**, `acks=all` | §6, and `ADR-0019` for authorization records |
| **Manual offset commit**, auto-commit forbidden | §6 — auto-commit turns at-least-once into silent at-most-once on a crash |
| **Seek and rewind** a consumer group over an offset range | §5 replay policy, `AC-045` |
| **`seekToEnd` at startup** for one group and not others | §6.1, `DEC-002`, `ADR-0012` |
| **Admin operations**: create topics with pinned partitions, RF, retention and cleanup policy; read them back | `AC-026` |
| **Tombstones** — produce a null value under a key | `OD-008`, the decommission rule |
| **Headers** on produce and consume | §9 DLQ headers |
| A broker a developer and CI can run | everything above |

## Decision drivers

1. **Permissive licence, verified in the package**, not only in a repository (`TOOLCHAIN.md` records
   the OPC UA case where those differed).
2. Every semantic above expressible **directly**, not through a framework's abstraction.
3. Pinned exactly, with a lock file, like every other dependency.
4. The local runtime must be the **same software** as production-like, differing only in
   replication factor and resources — a local stand-in with different semantics would make the
   ordering and replay tests prove nothing about the real thing.
5. Nothing added that Phase 3 does not need. `OD-009` already bounded the one component this phase
   builds; a library that arrives with its own consumer pipeline undoes that decision.

## Considered options

### Client library

| | Option | Licence | Verdict |
|---|---|---|---|
| 1 | **`Confluent.Kafka` 2.15.1** | **Apache-2.0** | **chosen** |
| 2 | `KafkaFlow` 4.2.0 | MIT | rejected — a consumer **framework** (middleware pipeline, handler registry, typed serialisers) built *on* Confluent.Kafka, which it takes as a dependency. It would sit exactly where `OD-009`'s bounded component sits, and that decision was explicitly "not a consumer framework" |
| 3 | Hand-rolled Kafka protocol client | — | rejected, and not narrowly. `ADR-0020` rejected hand-rolling a Modbus **server** for risks that were mostly about concurrency and framing; the Kafka wire protocol is an order of magnitude larger, versioned per API key, and its client owns group membership, rebalancing and offset management. `OD-006` let a hand-written **FC04 reader** in because it is one request and one response; nothing here is that shape |

`Confluent.Kafka` is the client Confluent maintains and the one the broker's own semantics are
documented against. It wraps `librdkafka`, which is where idempotence, the transactional producer and
the group protocol actually live.

### Local broker runtime

| | Option | Licence | Verdict |
|---|---|---|---|
| 1 | **`apache/kafka:4.3.1` in KRaft mode**, under `deploy/compose/` | Apache-2.0 | **chosen** — the Apache image, no ZooKeeper (Kafka 4.x is KRaft-only), and the same distribution a production-like deployment runs |
| 2 | Redpanda | **BSL 1.1** | **disqualified.** Source-available with usage restrictions, not permissive. `TOOLCHAIN.md` records that every one of the 29 packages in the graph is MIT or Apache-2.0; a BSL component would end that claim, and its API-compatibility is not the point — the licence is |
| 3 | Confluent Community images (`cp-kafka`) | Confluent Community Licence | rejected — not permissive either, and unnecessary when the Apache image exists |
| 4 | An embedded or in-memory Kafka stand-in | — | rejected — driver 4: different software, and the replay and ordering tests would prove nothing about a real broker |

### Integration-test lifecycle

| | Option | Licence | Verdict |
|---|---|---|---|
| 1 | **`Testcontainers` + `Testcontainers.Kafka` 4.15.0** | **MIT** | **chosen** for the Phase 3 test suites (`KAFKA-001`, `KAFKA-002`, `FAIL-KAFKA-001`): each run gets a fresh broker on a random port, which is what makes `AC-026`'s "create from nothing, then run again unchanged" assertable at all |
| 2 | `docker compose up` from the test harness | — | rejected as the default — a shared long-lived broker carries state between runs, and `AC-026`'s idempotence assertion is only meaningful against an empty broker. Compose stays for running the system by hand |
| 3 | A shared CI broker service | — | rejected — same state problem, plus cross-job interference |

Both use the **same image tag**, so the container the tests run is the container a developer runs.

## Decision

| Component | Package / image | Version |
|---|---|---|
| all Kafka producers and consumers | `Confluent.Kafka` | `2.15.1` |
| integration tests | `Testcontainers` | `4.15.0` — **not** `Testcontainers.Kafka`, see the amendment below |
| local and CI broker | `apache/kafka` (KRaft) | `4.3.1@sha256:77e3df9054047a88b520d0cc46e16696d3b22022e1d580aeccd2632df6532837` |

**Testcontainers is given that same image explicitly.** Its Kafka module has its own default image,
which is not this one; left implicit, the integration tests would run a different distribution from
the one compose runs, which is exactly what driver 4 forbids. The image reference — tag **and**
digest — lives in one place in the test project and is asserted, not assumed.

Pinned exactly in `TOOLCHAIN.md` and locked by `packages.lock.json` **when this ADR is accepted**,
not before — `ADR-0020` set that order and Phase 1's gate rule is the reason.

The image is pinned **by tag and digest**. A tag is mutable; a digest is the only thing that makes
"the same broker" reproducible, and this repository has already argued that a floating container tag
is the same defect as an unpinned transitive package.

## Rationale

**Why not a framework (option 2).** `OD-009` decided Phase 3 owns one bounded component — validate,
DLQ on first attempt, commit after the DLQ produce — and wrote down that it is *not* a consumer
framework, because a framework is how the poison-message rule ends up implemented twice and
differently. Adopting `KafkaFlow` would import exactly that layer from outside, and the repository
would then have two answers to "where does a consumer's error handling live".

**Why the licence disqualifies Redpanda rather than its behaviour.** Redpanda is Kafka-API
compatible and would very likely pass these tests. `NFR-012` and the licence inventory are not about
whether a component works; they are about what may be depended on. Recording "we rejected it for the
licence" is more honest than inventing a technical objection.

**Serialisation is not part of this decision, and saying so is the decision.** Records are
**raw UTF-8 JSON bytes**, validated against the repository's own JSON Schemas (`ADR-0017`). No
serialiser package, no schema registry, no Avro or Protobuf on these topics: `OD-009` put validation
in the shared consume path, and a registry would make a second authority for schemas that
`ADR-0017` says live here. The DLQ carries the **original bytes**, unparsed — a payload that failed
validation cannot be re-serialised without inventing what it meant.

**What the choice does not settle.** `Confluent.Kafka` ships the native `librdkafka.redist`, whose
NuGet metadata uses the **deprecated `licenseUrl` form** pointing at `LICENSES.txt` rather than an
SPDX expression, so `scripts/license-scan.mjs` will report it as unidentified. That file also covers
the components librdkafka statically links. The OPC UA packages taught this lesson once already —
the licence that matters is the one **in the package** — so verifying it is an acceptance condition
below, not an assumption.

## Trade-offs

- `Confluent.Kafka` binds a **native** library. It carries platform-specific binaries, and a
  container base image without the right libc is a real failure mode. Phase 10 has to check it; the
  demo profile on Windows and Linux x64 is covered by the redistributable.
- `Testcontainers` requires a Docker daemon wherever the tests run, including CI. The alternative
  was a shared broker, which trades that requirement for cross-run state.
- Kafka 4.x is KRaft-only. No ZooKeeper is a simplification, but it also means the topology's
  `RF 1 / 3` split is the only difference between local and production-like, which is the point.

## Reliability impact

Idempotent producers plus manual commits are what make the at-least-once contract in §6 real. The
client's own retry and timeout defaults are **not** inherited: §11 fixes those numerically and the
producer and consumer configurations state them explicitly, because a default that changes between
client versions is an undocumented change to the failure model.

## Failure impact

Kafka is `CONTROL-INDEPENDENT` (F06) and this ADR does not change that. The gateway's egress buffer
absorbs a broker outage and drops oldest (`EDGE_GATEWAY.md` §13); nothing on the command path
consults Kafka (`ADR-0009`).

## Security impact

`SECURITY_BOUNDARIES.md` requires TLS + SASL with per-service principals and per-topic ACLs.
`Confluent.Kafka` supports SASL/SCRAM and mTLS through `librdkafka`. The **demo profile runs
PLAINTEXT on a private compose network**, exactly as the OPC UA demo profile runs `SecurityPolicy
None`, and switching profiles is a `SECURITY_BOUNDARIES.md` decision rather than a code change.

## Amendment 2026-09-29 — the Kafka module cannot start the pinned image

`Testcontainers.Kafka` 4.15.0 does not work with `apache/kafka:4.3.1`. Its Apache path writes the
advertised listeners into a startup script **after** the container is running — the mapped port is
not known before that — while the Apache image formats its storage in its own entrypoint and exits
first:

```
Exception in thread "main" org.apache.kafka.common.config.ConfigException:
Configuration 'advertised.listeners' values must not be empty.
    at kafka.tools.StorageTool$.execute(StorageTool.scala:79)
```

Reproduced with and without the digest, and with a placeholder `KAFKA_ADVERTISED_LISTENERS`
environment variable, which the module overrides. The same image starts correctly from a plain
`docker run` with the variables the broker actually needs.

**Decision: use the `Testcontainers` core package and define the container explicitly**
(`EventBackbone.Tests/KafkaBroker.cs`). The port is chosen by the test before the container starts,
so the advertised listener is correct at format time. This also settles B7 by construction: the
image is named in one place and there is no module default to drift onto — the module's default is
`confluentinc/cp-kafka`, a different distribution from the one `deploy/compose/` will run.

The alternative — switching the pinned image to `confluentinc/cp-kafka` so the module works — was
rejected: it trades the Apache distribution this ADR chose for the convenience of a test helper,
and the Confluent Community licence was already rejected above.

## Evidence gathered before acceptance

Checked on 2026-09-28, **without adding anything to this repository** — no package reference, no
lock file, no manifest. Phase 3 has not been requested, and `CLAUDE.md`'s "authorized is not the
same as started" makes that distinction the one that matters. Packages were downloaded to a
scratch directory and read there.

**1. `Confluent.Kafka` 2.15.1 targets `net10.0` explicitly.** Its dependency groups are `net10.0`,
`net8.0`, `.NETStandard2.0` and `.NETFramework4.6.2` — so this is a real target, not a
`netstandard2.0` fallback, and the only dependency it brings is `librdkafka.redist [2.15.1, )`.

**2. `librdkafka.redist` 2.15.1's packaged `LICENSES.txt`, read from inside the `.nupkg`.** Fifteen
sections, every one permissive:

| Licence | Components |
|---|---|
| BSD-2-Clause | librdkafka itself, `lz4`, `wingetopt` |
| BSD-3-Clause | `queue`, `snappy` |
| MIT | `cjson`, `hdrhistogram`, `murmur2`, `pycrc` |
| Apache-2.0 | `opentelemetry` |
| zlib-style / public domain | `crc32c`, `fnv1a`, `nanopb`, `regexp`, `tinycthread` |

**3. And the finding that matters: `LICENSES.txt` does not cover everything in the package.** The
Windows runtimes ship binaries that file says nothing about — `libcrypto-3` and `libssl-3`
(OpenSSL 3), `libcurl`, `z`, `zstd`, and Microsoft's `msvcp140` / `vcruntime140`. The first four are
permissive, and the MSVC runtime is redistributable under Microsoft's own terms rather than an open
licence. This is the same shape as the OPC UA finding in `TOOLCHAIN.md` — the licence that governs
is the one in the artefact you actually ship — and it is why condition B3 below asks for the
**binaries**, not just the source manifest.

**4. Image digest, from two independent sources.** `docker buildx imagetools inspect` and the Docker
Hub API both give `apache/kafka:4.3.1` as
`sha256:77e3df9054047a88b520d0cc46e16696d3b22022e1d580aeccd2632df6532837` (pushed 2026-06-23).

## Acceptance conditions

Split in two after the Codex challenge on how to accept this ADR
(`reviews/phase-3/CODEX_ADR-0021_PATH_raw.md`). Group A is evidence, and it is done — the section
above. Group B cannot be verified without writing Phase 3 code, and follows `ADR-0020`'s precedent:
that ADR was accepted as a decision and carried its unverifiable items as "open items for the
Phase 2 spike", which later commits closed.

**Nothing may be added to the repository until the product owner requests Phase 3** — no package
reference, no lock file, no compose file, no test project. If any Group B condition turns out to be
false, Phase 3 stops and this ADR is amended rather than worked around.

### Group A — verified above

| | Condition | Result |
|---|---|---|
| A1 | `Confluent.Kafka` 2.15.1 targets net10.0 | **yes**, an explicit target group |
| A2 | `librdkafka.redist`'s packaged licence file read | **done** — 15 sections, all permissive |
| A3 | The image pinned by digest | **done** — recorded in the decision table |

### Group B — Phase 3 spike conditions

**B1 — the licence graph, restored. DONE 2026-09-28.** 48 packages, all permissive.
`TOOLCHAIN.md` is updated, including a correction: `MS-PL` was already in the Phase 2 graph and the
table had not said so.

**B2 — the scanner must not pass this silently. DONE 2026-09-28.** `scripts/license-scan.mjs` is a
gate: an unclassifiable licence fails unless `scripts/license-evidence.json` records what was read
and where, and stale evidence fails too. Both failure paths were tested by removing the evidence and
by adding an entry for a package that is not in the graph.

**B3 — the shipped binaries, not just the source manifest. DONE 2026-09-28.** Recorded in
`TOOLCHAIN.md`. The deployed runtime is Linux, where the package ships a single `librdkafka.so` with
those components statically linked; the OpenSSL, libcurl, zlib, zstd and MSVC runtime binaries are
in the **Windows** runtimes only, so no Microsoft redistributable reaches a container image.

**B4 — partition-count immutability is ours to enforce, not Kafka's.** `AdminClient` offers
`CreatePartitionsAsync` and the broker will honour it. The bootstrap must describe first and fail
**before** any mutating call, and must never call it at all. `KAFKA-001`'s third run is the negative
case.

**B5 — every semantic in the Context table proved by a smoke test**: idempotent producer with
`acks=all`, manual commit, seek and rewind, `seekToEnd` on the Supervisor's group **after**
assignment (§6.1 — before assignment it silently does nothing), a null-valued tombstone (`OD-008`),
and headers surviving a round trip. **PARTIAL 2026-09-29** — proven by `ContractConsumerTests`:
the idempotent `acks=all` producer, manual commit (the committed offset is asserted, including
that it does **not** advance when the DLQ produce fails), and headers surviving onto the DLQ
record. **Still open:** seek and rewind (`AC-045`), `seekToEnd` after assignment, and tombstones —
each lands with the work that needs it.

**B6 — the consumer group protocol is pinned. DONE 2026-09-29.** `ContractConsumer` overrides
whatever it is given with `group.protocol=classic` and `partition.assignment.strategy=range`, plus
auto-commit and auto-offset-store off and topic auto-creation off. Classic and eager rather than
KIP-848 or cooperative-sticky: replay (`AC-045`) rewinds a whole group over a known range, and an
eager rebalance is the model that is simplest to reason about for that. The choice is revisitable;
inheriting it silently is what B6 forbids.

**B7 — Testcontainers is given the pinned image. DONE 2026-09-29.** Satisfied by dropping the Kafka
module: the container is defined explicitly and the image constant lives in one place
(`KafkaBroker.Image`). See the amendment above for why the module could not be used at all.

**B8 — Docker-dependent tests fail loudly. PARTIAL 2026-09-29.** The "fail, don't skip" half is
**observed**: with the Docker daemon stopped, all three `TopicBootstrapTests` failed rather than
skipping. The "run in CI" half could not be met — **the repository had no CI configuration
at all** — and that was the finding, not a detail. `ADR-0023` adds it (2026-09-29); the `dotnet` job
checks for a Docker daemon first and fails without one. **B8 closes on the first green run on
GitHub**, not on the commit that adds the workflow. A suite that silently skips when Docker is missing has stopped
being evidence — `LOAD-001`'s `MAIR_LOAD_TEST` gate is opt-**in** for a long run, not a precedent
for skipping correctness tests.

## Sources

- `Confluent.Kafka` — https://www.nuget.org/packages/Confluent.Kafka/2.15.1 (Apache-2.0, published 2026-09-10)
- `librdkafka.redist` — https://www.nuget.org/packages/librdkafka.redist
- `Testcontainers.Kafka` — https://www.nuget.org/packages/Testcontainers.Kafka/4.15.0 (MIT)
- `KafkaFlow` — https://www.nuget.org/packages/KafkaFlow/4.2.0 (MIT)
- Apache Kafka 4.3.1 release — https://kafka.apache.org/downloads (released 2026-06-25)
- `apache/kafka` image — https://hub.docker.com/r/apache/kafka (tag `4.3.1`, image pushed 2026-06-23)
- Kafka 4.x is KRaft-only — https://kafka.apache.org/documentation/#upgrade
- Redpanda licensing — https://github.com/redpanda-data/redpanda/blob/dev/licenses/bsl.md
