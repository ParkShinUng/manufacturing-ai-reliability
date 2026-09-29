# ADR-0022 — how .NET services validate records against the contract schemas
Status: **Accepted** (2026-09-29, by the product owner, after a spike) — **`Corvus.Text.Json.Validator`
5.6.1.** Revised the same day after a Codex challenge that rejected the first recommendation ([`reviews/phase-3/CODEX_ADR-0022_CHALLENGE_raw.md`](../../reviews/phase-3/CODEX_ADR-0022_CHALLENGE_raw.md)).
`major dependency` and `schema versioning` are on the mandatory participation list
(`DUAL_AGENT_PROTOCOL.md` §2).
Phase: needed by Phase 3's shared consume-validate-DLQ component (`OD-009`, `AC-027`).

## Context

`OD-009` put schema validation in one shared consumer path, and `AC-027` requires a schema-invalid
record to reach the DLQ on the first attempt. `ADR-0017` makes the JSON Schemas in
`contracts/jsonschema/v1/` authoritative, and they declare **draft 2020-12**.

Nothing in the repository can do that today. `tests/contract/validate_examples.mjs` is a
deliberately hand-rolled **subset** validator in Node, written dependency-free so it could run
during the specification phase; it is not available to a .NET consumer, and `ADR-0021` selected
only a Kafka client.

**What the schemas actually use.** Counted across all eight, 23 distinct validation keywords:
`type` 195 · `format` 49 · `minimum` 46 · `properties` 40 · `required` 36 · `maximum` 35 ·
`const` 31 · `additionalProperties` 22 · `enum` 20 · `exclusiveMinimum` 15 · `minLength` 14 ·
`pattern` 12 · `items` 8 · `uniqueItems` 5 · `minItems` 4 · `if` 3 · `then` 3 · `allOf` 1 ·
`not` 1 · `minProperties` 1 · `maxItems` 1 · `prefixItems` 1 · `maxLength` 1.

## What was measured

Each candidate was run outside the repository against `telemetry.schema.json` and the healthy
example, with mutations the contract must reject.

| Case | `NJsonSchema` 11.6.1 | `JsonSchema.Net` 9.4.0 | `Corvus.Json.Validator` 5.6.1 |
|---|---|---|---|
| valid example | accepted | accepted | accepted |
| `schemaVersion: 2` vs `const: 1` | **accepted — not detected** | rejected | rejected |
| bad enum value | rejected | rejected | rejected |
| `eventId` removed | rejected | rejected | rejected |
| unexpected property | rejected | rejected | rejected |
| `temperatureC: 9999` vs `maximum` | — | — | rejected |
| packages added to the graph | — | **4** | **36**, including Roslyn |
| licence | MIT | MIT **source**; the NuGet **binary** ships `OSMFEULA.txt` | Apache-2.0 |

**`NJsonSchema` silently ignores `const`** — a known upstream defect (RicoSuter/NJsonSchema#1857).
`const` pins `eventType` and `schemaVersion` on every schema, so a validator that ignores it accepts
records the contract rejects: the exact failure `AC-027` exists to catch, arriving through the
component built to prevent it.

**`JsonSchema.Net`'s licence needs precise words.** The source is MIT, and the fee is explicitly
*not* a licence fee: self-compiled binaries stay MIT. What ships on NuGet is a **binary
maintenance-fee agreement** requiring payment from users whose revenue-generating use exceeds
US$10 000 a year. A portfolio with no revenue is exempt. But `TOOLCHAIN.md` states the graph holds
no membership-gated licence, and that sentence would stop being true.

## Options

| | Option | Cost |
|---|---|---|
| **A** | `JsonSchema.Net` 9.4.0 | correct; 4 packages; the fee agreement must be accepted knowingly and `TOOLCHAIN.md` amended so its licence claim stays accurate |
| **B** | `NJsonSchema` 11.6.1 | **disqualified by measurement.** A wrapper adding our own `const` check would be trusting a validator whose remaining gaps are unknown and silent |
| **C** | A subset validator in this repository | **withdrawn — see below** |
| **D** | Generate DTOs and rely on deserialisation | rejected: shape only, no `const`, ranges, enums, `format` or `additionalProperties` |
| **E1** | `Corvus.Json.Validator` 5.6.1, runtime | correct; Apache-2.0; **36 packages**, and it compiles schemas with Roslyn at runtime — startup cost and a code-generation surface inside a plant-floor consumer |
| **E2** | `Corvus.Json.SourceGenerator` 5.6.1, build time | same engine, generating validation code from the authoritative schemas at **build** time. Runtime graph is **7** packages (`Corvus.Json.ExtendedTypes` plus `JsonReference`, `UriTemplates`, `NodaTime`, `CommunityToolkit.HighPerformance`, `Corvus.HighPerformance`, `Microsoft.Extensions.ObjectPool`), all Apache-2.0 or MIT, no Roslyn at runtime. Unmeasured: whether generated types give the DLQ path what it needs — the original bytes and a reason — which is a spike, not an assumption |

### Why option C is withdrawn

The first version of this ADR recommended a hand-rolled subset validator at "~150 lines", justified
by the PRNG and the FC04 reader. Codex rejected that, and it was right on all three counts:

- **The surface was understated.** 23 keywords, `format` in 49 places, plus `if`/`then`, `allOf`,
  `not` and `prefixItems`. Correct applicator semantics is not 150 lines, and a validator that gives
  up some of them makes `AC-027` mean "invalid under our subset", not "invalid under the contract".
- **The cross-check was weak.** Checking a new subset validator against the Node subset validator
  can only prove they share blind spots; the Node one does not fail on unsupported keywords either.
- **The precedent was the wrong one.** `OD-006`'s FC04 reader is a bounded sliver — one connection,
  one request, one function code. JSON Schema is an externally specified language that grows.
  `ADR-0020` rejected hand-rolling the Modbus **server** for exactly that shape of risk, and this
  resembles the server, not the reader.

Recorded rather than deleted, because the reasoning that produced it — licence discomfort with A
looking for a technical justification — is the kind of motivated reasoning this process exists to
catch.

## Decision — E2, as the spike found it: `Corvus.Text.Json.Validator` 5.6.1

The product owner chose E2 on condition that a spike ran first. It did, outside the repository,
and it changed the specific package without changing the choice. `Corvus.Json.SourceGenerator`'s
own README says it is the **V4** line and points new projects at the V5 `Corvus.Text.Json` family.
That family has a runtime validator, and it is the better answer on every axis this ADR measures:

| | Result |
|---|---|
| the valid telemetry example | accepted |
| `const`, `enum`, `required`, `additionalProperties`, `maximum`, `format: uuid` violations | **all rejected** |
| a legitimately empty array | accepted — correctly |
| all eight schemas under `contracts/jsonschema/v1/` | load |
| validates **raw UTF-8 bytes** (`Validate(ReadOnlyMemory<byte>)`) | yes — the bytes validated are the bytes that reach the DLQ, unparsed and unchanged, and that was checked |
| a reason for the DLQ | yes, through `JsonSchemaResultsCollector` |
| packages added | **3** — the validator, `Corvus.Text.Json`, `NodaTime` |
| licence | Apache-2.0 |

It is smaller than the V4 source generator's runtime (7), far smaller than the V4 runtime validator
(36, with Roslyn), about the size of `JsonSchema.Net` (4), and carries no fee agreement. There is no
build-time generation step to maintain either: the schemas are loaded from
`contracts/jsonschema/v1/` as they are, so the file the contract tests validate is the file the
consumer validates against.

**One behaviour the component has to own.** Input that is not JSON at all does not return `false` —
it throws `JsonReaderException`. A consumer that did not catch it would crash on a single corrupt
record, which is the opposite of what the DLQ exists for. Catching it and routing the record to the
DLQ with a parse-failure reason is condition 5 below.

## Recommendation, as it stood before the spike

**E2 if its spike succeeds, otherwise A.** E2 keeps the licence claim intact and the runtime graph
small, and generates from the same authoritative schemas the contract tests use. A is the smaller
change and is correct today; choosing it means accepting a fee obligation for any commercial
adopter and saying so in `TOOLCHAIN.md` rather than leaving a stale claim.

E1 is not recommended: Roslyn at runtime inside a consumer is a large surface for something a build
step can do.

## Conditions

1. **The validator ships with per-keyword negative fixtures** — one invalid document per keyword in
   the inventory above, each proven to be rejected. `AC-027` proves DLQ routing, not validator
   correctness; without this suite a validator that catches one fixture satisfies `KAFKA-002` while
   still accepting other invalid records.
2. **Valid fixtures for every schema**, so a validator that rejects everything cannot pass either.
3. **`KAFKA-002` uses the production validator**, not a test double.
4. **The licence inventory is re-derived** after the packages are added, and `TOOLCHAIN.md` is
   corrected rather than left claiming what it claimed before.
5. **Unparseable input is a DLQ reason, not a crash.** `JsonReaderException` is caught inside the
   shared component and the record goes to the DLQ with the original bytes and a parse-failure
   reason; a test proves the consumer carries on afterwards.

## Sources

- keyword inventory — counted across `contracts/jsonschema/v1/*.json`
- `NJsonSchema` 11.6.1 — https://www.nuget.org/packages/NJsonSchema/11.6.1 · `const` defect: https://github.com/RicoSuter/NJsonSchema/issues/1857
- `JsonSchema.Net` 9.4.0 — https://www.nuget.org/packages/JsonSchema.Net/9.4.0/License · agreement text: https://github.com/json-everything/json-everything/blob/master/OSMFEULA.txt
- `Corvus.Json.Validator` 5.6.1 and `Corvus.Json.SourceGenerator` 5.6.1 — https://github.com/corvus-dotnet/Corvus.JsonSchema (Apache-2.0)
