# Phase 2 — classification of round 1, and request for round 2

> Round 1: `CODEX_VERIFICATION_raw.md`, verdict **REJECT** (1 P0, 9 P1, 1 P2). Every finding was
> checked against the code before it was classified; none was accepted on Codex's word alone.
> `DEFINITION_OF_DONE.md`: P0 = 0 and P1 = 0, every finding classified.

## Codex findings

| ID | Sev | Classification | What changed |
|---|---|---|---|
| COD-P2-001 | P0 | **ACCEPTED** | The k-th-value pairing is gone. Monitored items use the `StatusValueTimestamp` trigger, so every node reports on every step; the server publishes each step once, all ten nodes stamped with one instant; `OpcUaSampleAssembler` emits exactly the ten values sharing the marker's source timestamp, holds an incomplete step 2 cycles, then drops and counts it. In order; late values discarded and counted; a `Bad_*` marker is not a sample. `OT_PROTOCOL_MAPPING.md` §1.3 amended. |
| COD-P2-002 | P1 | **ACCEPTED** | `_handles` is filled as items are added, before `CreateAsync`, and only read afterwards. |
| COD-P2-003 | P1 | **ACCEPTED** | `OpcUaSampleAssemblerTests` drives the rule deterministically — uneven queue depths, a discard from one item only (the exact case k-th pairing stitched), straddles, hold expiry, late values. The wire test is now a theory at 120, 60 and 20 ms steps; 20 ms is faster than sampling. |
| COD-P2-004 | P1 | **ACCEPTED** | `OpcUaSubscriptionTelemetrySource` is the OPC UA path. `ITelemetrySource.ReadAsync` returns 0..n records. A lapsed keep-alive (`PublishingStopped`) is turned into a read failure, so a dead endpoint reaches `OFFLINE` instead of looking idle. |
| COD-P2-005 | P1 | **ACCEPTED** | `CrossProtocolTests` asserts AC-021 on the subscription as well as the batch read. |
| COD-P2-006 | P1 | **ACCEPTED** | `LoadScenarioTests.Ac001_*` runs 20 equipment per protocol through real servers, clients and loops in every suite run, with the same per-record canonical assertions (`LiveRig.AssertCanonical`). The Modbus one **exposed OD-006** below. |
| COD-P2-007 | P1 | **ACCEPTED** | LOAD-001 asserts the OD-005 matrix: Modbus — every skip flagged, zero read failures; OPC UA — zero gaps after a **measured** warm-up, zero buffer drops. Run per protocol, sequentially: both in one process measured rig contention (halved poll rate). |
| COD-P2-008 | P1 | **ACCEPTED** | `Drain` runs outside the protocol `try`; sink exceptions are `EgressFailures` / `LastEgressFailure`; peek-emit-dequeue keeps a refused record for retry, drop-oldest bounds it. Two tests. The `FailingSink` test double already existed and was **used by no test** — the claim it was named for was unproven. |
| COD-P2-009 | P1 | **PARTIALLY_ACCEPTED** | The exporter and metrics contract are Phase 8 by `IMPLEMENTATION_PLAN.md`, so it is not built here. What was wrong is that nothing said so: `EDGE_GATEWAY.md` §17 now maps each §17 metric to its Phase 2 counter, and AC-023 states Phase 2 proves the counter, not its export. |
| COD-P2-010 | P1 | **ACCEPTED** | `EndpointOutageTests`: both protocols, real endpoints torn down and rebuilt on the same port. The outage is 10 s rather than 30 s — the backoff saturates at its cap after 7.75 s, so longer outages repeat the same worst case; recorded in `TEST_SPECIFICATIONS.md`. |
| COD-P2-011 | P2 | **ACCEPTED** | One frame builder, `OpcUaFrame.From`, for both OPC UA routes. |

## Found by Claude while fixing

| ID | Sev | Classification | Finding |
|---|---|---|---|
| CLD-P2-001 | **P0** | fixed | **The OPC UA status flags never reached the event.** `OpcUaFrame.StatusFlags` had no consumer: the normaliser's OPC UA overload took values only. An `Uncertain_*` reading went out **GOOD**, and `Bad_NoCommunication` became `SENSOR_MISSING` instead of `SENSOR_DISCONNECTED`. Codex's round 1 and the existing dead-sensor test both missed it, the test because `Bad_OutOfService` → null → `SENSOR_MISSING` happens to be the same flag either way. `Normalise(OpcUaFrame)` now carries them; tests assert the Uncertain, LocalOverride and NoCommunication cases. |
| CLD-P2-002 | P2 | fixed | §1.5: with no `SourceTimestamp`, both OPC UA routes substituted `DateTime.UtcNow` **without** `TIMESTAMP_SYNTHESISED` — the gateway clock passed off as the source clock. `SourceTimeUtc` is now nullable and the normaliser raises the flag. |
| CLD-P2-003 | P1 | **HUMAN_DECISION_REQUIRED** — `OD-006` | NModbus's `ReadInputRegistersAsync` is `Task.Factory.StartNew` over a blocking read (decompiled). Twenty loops starve the thread pool: 12 timeouts in 20 s; with the pool minimum raised, 0 failures, 0 overruns, gaps 325 → 38. A dependency and concurrency decision, so options, not a fix. |
| CLD-P2-004 | P2 | open, for Codex | `FAIL-OT-001` says "state → `OFFLINE`". Phase 2 has no `factory.equipment-states.v1` record, so the test asserts `protocol_connected`; noted in the specification. Is that an honest reading, or a Phase 2 scope gap? |

## Round 2 — what to verify

1. **The new coherence rule.** Is "exactly the ten values sharing the marker's `SourceTimestamp`"
   sound given how the OPC Foundation server samples? What if two steps share a timestamp (clock
   resolution), or the server's sampled `SourceTimestamp` is not the node's `Timestamp`? Is a hold
   of 2 cycles justified, and is the late-value path reachable in a way that loses more than it
   should?
2. **The server change** — each step published once, by sample reference. Can it suppress a real
   update?
3. **CLD-P2-001.** Is the status-flag merge right — null with a status flag gets no `SENSOR_MISSING`;
   a value keeps its range, frozen and outlier checks and gains the status flag?
4. **`OpcUaSubscriptionTelemetrySource`.** Is `PublishingStopped` the right liveness signal? Does
   reconnect leak a subscription or a session?
5. **Egress isolation.** Peek-emit-dequeue under a sink that fails forever — bounded?
6. **Do the new tests prove their claims** — in particular the outage tests' timing bounds and the
   20 ms wire theory, which drops samples by design.
7. **OD-006.** Challenge the cause, the measurement, and the three options. The recommendation is B.
   This is on the mandatory list; the product owner decides.
8. **CLD-P2-004.**
9. Anything else, including whether any round-1 finding is not actually fixed.

Output format as before: `ID · severity · file:line · issue · why · fix`, then a verdict.

---

# Round 2 result — `CODEX_ROUND2_raw.md`, verdict **REJECT** (2 P1)

Codex confirmed COD-P2-001 through 011 and CLD-P2-001/002 fixed in substance, and CLD-P2-004 an
honest Phase 2 scope reading.

| ID | Sev | Classification | What changed |
|---|---|---|---|
| COD-P2-R2-001 | P1 | **ACCEPTED** | The coherence key assumed `DateTime.UtcNow` differs per step. The server now stamps each equipment's steps strictly increasing (+100 ns on a collision), `OT_PROTOCOL_MAPPING.md` §1.3 states the uniqueness rule and that "zero gaps" assumes steps ≥ 50 ms apart, and the wire coherence theory gained a burst case — five steps back to back, then a pause. |
| COD-P2-R2-002 | P1 | **HUMAN_DECISION_REQUIRED** — `OD-006` | Codex independently confirmed the cause by decompiling, and takes **option B**; A only as an interim, C keeps the thread-per-connection ceiling. Claude and Codex agree, which is not a decision: the product owner decides. |

Round 3 waits for OD-006, so that one verification covers both.

---

# OD-006 resolved — option B, by the product owner. Request for round 3

**What changed**

- `ModbusTelemetryClient` is an in-repository async FC04 reader. One connection, one request
  outstanding, FC04 only. Transaction id, protocol id, unit, function code, MBAP length and byte
  count must all match the request exactly. Any mismatch or timeout closes the socket. A well-framed
  exception PDU keeps the connection and surfaces as `ModbusException.ExceptionCode`.
- `NModbus` removed from the gateway project; the simulator keeps it. The lock file is updated.
- `ADR-0020` amended, with the reasoning for why its rejection of hand-rolled Modbus was a
  **server** argument that does not reach a one-request client. `TOOLCHAIN.md` and
  `OT_PROTOCOL_MAPPING.md` §2.7 are updated.
- `ModbusFramingTests` — 11 cases against a scripted server that misbehaves on purpose.
- Measured, 20 s, 20 equipment, default pool: 0 read failures, 0 overruns, 0 gaps, 100 % coverage.
  It was 12 failures and 325 gaps on NModbus.

**Found while doing it.** The silent-server test failed only in the full suite. The cause was a
`Task.Delay(Timeout.Infinite)`, which has no timer behind it, so nothing rooted the handler holding
the server socket. GC finalised it and the client saw a reset. Two other hypotheses were tested
first and disproved: parallel port reuse, and a shared port under address reuse.

**Round 3 — verify**

1. The FC04 reader against the risks ADR-0020 named. Is any framing case untested or mishandled?
   Is keeping the connection on an exception PDU right?
2. Whether the ADR amendment's argument holds, or whether it quietly reverses a decision it
   claims to keep.
3. COD-P2-R2-001: the strictly increasing server timestamps.
4. Whether anything from rounds 1 and 2 has regressed.
5. Anything else.

---

# Round 3 result — `CODEX_ROUND3_raw.md`, verdict **ACCEPT_WITH_CONDITIONS**

No P0 or P1, and no regression from rounds 1 or 2. COD-P2-R2-001 confirmed fixed; the ADR-0020
amendment confirmed to hold as a client-only exception. Both conditions are applied:

| ID | Sev | Classification | What changed |
|---|---|---|---|
| COD-P2-R3-001 | P2 | **ACCEPTED** | "One request outstanding" is enforced at the API boundary: an interlocked in-flight flag refuses a concurrent `ReadAsync` with `InvalidOperationException` before anything reaches the wire. Tested — the refused read sends nothing, and the first still gets its own reply. |
| COD-P2-R3-002 | P3 | **ACCEPTED** | A nonzero MBAP protocol id added to the malformed-response theory. |

Codex could not run `scripts/license-scan.mjs` in its sandbox (NuGet config ACL); Claude ran it on
the same tree: all permissive, the one known `xunit.abstractions` `licenseUrl` entry unchanged.

**Status: P0 = 0, P1 = 0, every finding classified.** 195 passed,
2 skipped, all four contract checks passing.
