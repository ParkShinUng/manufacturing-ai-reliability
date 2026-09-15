# Test Strategy

> **Executable detail is in [`TEST_SPECIFICATIONS.md`](TEST_SPECIFICATIONS.md)** — per-AC specs,
> 13 failure tests with assertions, load specs, and property tests. This document is the shape;
> that one is the content.
>
> Three suites are **runnable now**, before any application code exists:
> `validate_examples.mjs`, `consistency_check.mjs`, `safety_invariants.mjs` under `tests/contract/`.

## Pyramid
1. Unit tests: normalization, quality rules, safety gates, bounds, idempotency logic.
2. Contract tests: event/API schemas and compatibility fixtures.
3. Integration tests: simulator↔gateway, Kafka flow, prediction↔safety, control↔simulator.
4. Failure tests: dependency termination, stale/OOD data, duplicates, restart/replay.
5. Load tests: telemetry ingest, consumer lag, inference and control-decision latency.
6. End-to-end demo tests: named acceptance scenarios.

## Property-based testing (v0.3)

Six properties must hold for **any** input sequence, not just the enumerated cases — see
`TEST_SPECIFICATIONS.md` §6. The most important: no admissible command sequence may move the rate
outside 60-100 %, exceed 10 pp in one decision, or exceed 25 pp per 60 s window.

## Negative testing is mandatory

A check that cannot fail proves nothing. Every verification script must be demonstrated to reject a
crafted violation, and `safety_invariants.mjs` reports a **vacuously-passing invariant as a failure**.
This rule exists because the v0.3 review found two real bugs that three passing scripts had missed.

## Safety Supervisor testing
Use table-driven tests covering every gate and combinations. Default outcome for ambiguous/invalid required input is rejection/fallback, not AI acceptance.

## AI testing
Separate model-quality tests from system-safety tests. A high model metric cannot waive safety-gate tests.
