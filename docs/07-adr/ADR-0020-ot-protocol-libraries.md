# ADR-0020 — OPC UA and Modbus TCP libraries for the OT edge
Status: **Accepted** (2026-09-16, dual-agent reviewed)
Challenge: [`reviews/phase-2/CODEX_ADR-0020_CHALLENGE_raw.md`](../../reviews/phase-2/CODEX_ADR-0020_CHALLENGE_raw.md)
— `protocol choice` and `major dependency` are both on the mandatory participation list
(`DUAL_AGENT_PROTOCOL.md` §2). Codex challenged over several rounds; every finding was classified,
one was rejected with evidence (see *Findings rejected* below), and the rest were applied.
Phase: prerequisite for Phase 2 (`IMPLEMENTATION_PLAN.md`)

## Context
Phase 1 delivered the equipment simulator domain core with **zero external dependencies**, which is
what made it possible to start before this decision existed. Phase 2 adds the simulator-side
protocol **servers** and the Edge Gateway **clients**, and neither can be written without choosing a
library. `AGENTS.md` forbids adding a dependency without documented rationale, and NFR-012 requires
an ADR for new infrastructure components. No library is named anywhere in `docs/` or `contracts/`
today, so this ADR is the first place a name appears.

The choice is constrained by `OT_PROTOCOL_MAPPING.md`, which is already normative:

- OPC UA endpoint `opc.tcp://{host}:4840/mair/`, namespace `urn:mair:equipment:v1`, **one writable
  node only**;
- Modbus input registers based at `40 × equipmentIndex`, holding registers at `4 × equipmentIndex`,
  `sourceEpochMs` as a 32-bit value occupying `+20/+21`, `statusBitmap` at `+22`, read as a block of
  **23 registers**;
- and, from `EQUIPMENT_SIMULATOR.md` §11, an out-of-range setpoint write must be **rejected** with
  `Modbus exception 0x03` or OPC UA `Bad_OutOfRange` — **never silently clamped**, because clamping
  would mask a control-path defect that the Control Service bounds check should have caught first.

That last constraint is what decides the Modbus half. It is a rejection carrying a **value-dependent
exception code**, so a library that cannot see the written value before it lands is disqualified
regardless of how good it is otherwise.

## Decision drivers
- **License.** This is a public portfolio repository. A copyleft or membership-gated licence is
  disqualifying. The claim made below is deliberately narrow: **no copyleft or membership-gated
  licence was identified in the direct packages**, verified against the upstream licence files. The
  full transitive graph (Microsoft.Extensions.*, Newtonsoft.Json and similar) has **not** been
  scanned, and a lockfile-based licence scan is a pinning-time step, not an assumption of this ADR.
- **Value-dependent write rejection.** `EQUIPMENT_SIMULATOR.md` §11 requires exception `0x03`, not a
  clamp and not a generic `IllegalDataAddress`.
- **Server *and* client.** The simulator serves; the gateway reads. AC-021 requires both protocols
  to produce **identical canonical engineering values**, which is the test that catches word-order
  and scaling errors, so both sides must be controllable at the register level.
- **`net10.0`**, per `TOOLCHAIN.md`.
- Maintenance that is currently alive, not merely popular once.

## Considered options

### OPC UA
| | Option | Licence | Verdict |
|---|---|---|---|
| 1 | **UA-.NETStandard**, the OPC Foundation reference stack, consumed through its split packages (`….Server`, `….Client`, `….Configuration`) rather than the `OPCFoundation.NetStandard.Opc.Ua` meta package | **MIT** (OPC Foundation MIT License 1.00) | **chosen** |
| 2 | `Workstation.UaClient` (convertersystems) | MIT | client-only; the simulator needs a server |
| 3 | Hand-rolled OPC UA | — | not seriously considered: binary encoding, secure channel, session and subscription semantics are a certification-scale effort |

### Modbus TCP
| | Option | Licence | Verdict |
|---|---|---|---|
| 1 | **`NModbus`** | **MIT** | **chosen** |
| 2 | `FluentModbus` | MIT | **disqualified** — see below |
| 3 | `EasyModbusTCP.NET` | — | not evaluated further once option 1 met every driver |
| 4 | Hand-rolled Modbus TCP server + client | — | rejected; see *Alternatives rejected* |

## Decision

| Component | Package | Version |
|---|---|---|
| simulator (OPC UA server) | `OPCFoundation.NetStandard.Opc.Ua.Server` | `1.5.378.176` |
| edge gateway (OPC UA client) | `OPCFoundation.NetStandard.Opc.Ua.Client` | `1.5.378.176` |
| both (configuration and certificate handling) | `OPCFoundation.NetStandard.Opc.Ua.Configuration` | `1.5.378.176` |
| simulator and gateway (Modbus TCP) | `NModbus` | `3.0.83` |

`OPCFoundation.NetStandard.Opc.Ua.Core` comes in transitively and is not referenced directly.

The **split packages are named deliberately**: `OPCFoundation.NetStandard.Opc.Ua` is a meta package
with no framework assets of its own that pulls in GDS and complex-type assemblies neither service
needs. Referencing it would have imported a wider dependency surface than the design requires, which
is the opposite of what NFR-012 asks for.

Both are pinned exactly in `TOOLCHAIN.md`, never as a floating range.

## Rationale

**OPC UA — the reference stack, on licence and correctness.** It is the OPC Foundation's own
implementation and the one certification is defined against, it provides both server and client,
and `net10.0` is an explicit target framework as of `1.5.378.176` (published 2026-09-11), so the
toolchain baseline needs no exception.

The licence needed checking rather than assuming, because this stack has a widely-repeated history
of dual licensing — RCL for OPC Foundation corporate members and GPLv2 for everyone else — which
would have been disqualifying for a public portfolio repository. The current `LICENSE.txt` in
`UA-.NETStandard` is a **single MIT licence** (OPC Foundation MIT License 1.00) with no membership
condition. Secondary documentation still describes the old dual model in places, so the repository
licence file is treated as authoritative and the finding is recorded here rather than left to the
next reader to re-derive. This statement covers the **direct** packages only — see the licence
driver above.

**Modbus — `NModbus`, because `FluentModbus` cannot reject a bad value.** This is the whole of the
Modbus decision.

`FluentModbus` exposes its server state as a register buffer obtained with `GetHoldingRegisters()`.
A client write lands in that buffer directly. Its `RequestValidator` callback runs before the write
and can return `IllegalFunction` or `IllegalDataAddress`, but it receives only **unit id, function
code, address, and quantity — not the value**. `RegistersChanged` fires *after* the write. So an
out-of-range setpoint can be detected and corrected, but not **refused**: the write has already been
accepted at the protocol level by the time the value is visible. Correcting it afterwards is
precisely the silent clamp that §11 forbids.

`NModbus` routes slave writes through `ISlaveDataStore` → `IPointSource<ushort>.WritePoints(ushort
startAddress, ushort[] points)`, which receives the values, and
`InvalidModbusRequestException(byte exceptionCode)` carries an arbitrary Modbus exception code back
to the client. A custom point source can therefore validate the setpoint and throw exception `0x03`,
which is exactly the documented behaviour. It is MIT, has ~2.7M downloads, and its most recent
release is `3.0.83` (2026-04-16).

`NModbus` targets `net6.0`/`netstandard1.3`/`net46` rather than `net10.0` directly. That is a
consumption question, not a compatibility problem — a `net6.0` assembly loads on `net10.0` — but it
is recorded because it means the package is not being rebuilt against current targets, and that is
a maintenance signal worth watching rather than a defect today.

## Trade-offs

- **`NModbus` is less ergonomic than `FluentModbus`.** Implementing `ISlaveDataStore` by hand is
  more code than reading a buffer. That cost is accepted: it buys the one behaviour the
  specification names explicitly, and the register map in `OT_PROTOCOL_MAPPING.md` is hand-laid
  anyway, so a point source that maps `40 × equipmentIndex` and the `+20/+21` 32-bit split is work
  that has to happen in either library.
- **The OPC UA stack is large.** It is the largest dependency in the repository by far. Justified
  under NFR-012 because OPC UA is a requirement (FR-003/FR-005), not a convenience, and no smaller
  implementation is credible.
- **Two dependencies where the demo could have had one.** AC-021 requires both protocols precisely
  so that a scaling or word-order error in one is caught by disagreement with the other. Dropping
  one would delete the test.

## Reliability impact
None directly. Both libraries sit on the **control-independent** telemetry path; the L3 protective
layer delivered in Phase 1 has no dependency on either, which is what AC-020 asserts and what
Phase 1's zero-reference assembly test proves.

## Failure impact
`COMM_LOSS` (`FAILURE_MODEL.md`) is already specified and is implemented in the simulator as of
Phase 1. Phase 2 adds the reconnect path (AC-002, R-01: reconnect within 10 s **without a gateway
process restart**).

## Security impact
Challenging this ADR surfaced that the contract claimed an enforcement Modbus TCP cannot provide.
That is now OD-003, resolved as two listeners, and §2.1 of `OT_PROTOCOL_MAPPING.md` carries it.

Read-only gateway behaviour is server-enforced rather than trusted, but **by a different mechanism
per protocol**, and this paragraph originally asserted the OPC UA one for both:

- **OPC UA** — the gateway's session is provisioned without write permission on
  `OperationRateSetpointPct`. This is a stack configuration item the chosen library supports.
- **Modbus TCP** — there is no identity to withhold permission from, so the mechanism is the
  two-listener split (OD-003). The library is configured with two slave networks, and the one on
  `5020` simply has no writable point source.

Both are Phase 2 acceptance items, not assumptions to be carried. Neither prevents a gateway
**misconfigured** to the Modbus write port from writing; that is network policy's job and is
recorded as the residual risk in OD-003.

The OPC UA security policy for the endpoint is **not decided here**. `SECURITY_BOUNDARIES.md` governs
it, and choosing a policy is a separate decision from choosing a stack.

## Alternatives rejected

**Hand-rolling Modbus TCP.** Genuinely tempting: the repository needs read-only input registers plus
one writable holding register, MBAP framing is a 7-byte header, and the whole thing is perhaps 200
lines with zero dependencies — which would have preserved the property that made Phase 1 pleasant.
It is also not obviously inconsistent to reject it here while this repository *did* hand-roll its own
PRNG, so the distinction is worth stating rather than implying.

The PRNG was written because a dependency **could not** provide the required property: NFR-010 needs
bit-identical output for a seed across runtimes, and `System.Random` explicitly does not guarantee
its algorithm. It is ~40 lines, pure, and fully characterised by a property test. A Modbus TCP
server is the opposite on every axis: it is stateful, concurrent, exposed on a socket, and its risk
lives in transaction-id handling, partial and over-length frames, exception-response framing, and
multiple simultaneous connections.

`AC-021` was originally cited here as the reason, and that was the wrong evidence — it compares OPC
UA and Modbus engineering values, so it catches **scaling and word-order** disagreement and nothing
about framing or concurrency. The real reason is that nothing in the acceptance suite would catch a
hand-rolled framing bug, and writing the tests that would is a larger undertaking than adopting a
library that has already passed them.

**Hand-rolling OPC UA.** Not a real option, recorded only so the asymmetry with Modbus is explicit
rather than looking like an oversight.

## Contract defects this ADR surfaced

Challenging the ADR found two defects in `OT_PROTOCOL_MAPPING.md` itself. Both are corrected there,
because a library cannot be chosen against a contract that is not implementable.

1. **`FC06` cannot write the setpoint.** §2.4 said "function code 6/16", but
   `operationRateSetpointPct` is a **two-register int32**, and `FC06` writes one register. It could
   only ever write half the value — which is not a malformed request to be rejected, but a
   *different rate* the equipment would act on. Corrected to **`FC16` only, quantity exactly 2,
   starting exactly at the base**, with a response table covering every other case.
2. **Modbus TCP cannot enforce a read-only gateway.** Obligation 9 and `EQUIPMENT_SIMULATOR.md` §16
   **previously claimed** — both are now corrected — that the gateway's session is provisioned
   without write permission, so the property is server-enforced. Modbus TCP has no authentication, no session identity and no per-client
   permission model, so there is nothing to provision. This ADR had **repeated the claim without
   noticing**. Raised as `OPEN_DECISIONS.md` **OD-003** and **resolved the same day as option A**:
   two listeners, read-only `5020` for the gateway and write-capable `5021` for the Control Service.
   The library choice is unaffected — `NModbus` binds two `ModbusTcpSlaveNetwork` instances as
   readily as one, and the read-only network simply has no writable point source.

## Findings rejected

One Codex finding was **`REJECTED_WITH_EVIDENCE`**, and it is recorded rather than quietly dropped.

The final sweep flagged the word **"session"** in ten places — the `§3.1` state table's
"No protocol session", `T11`'s `session_timeout`, "gateway client sessions", and similar — as
implying Modbus access is session-based.

Rejected, because it conflates two different things. The defect this review actually found was about
**authorization**: claiming a permission model Modbus does not have. In these ten places "session"
means **connectivity**, which both protocols do have; it is imprecise, not false. `session_timeout`
is also a normative constant referenced by the transition table, the state diagram, the C#
implementation and its tests, so renaming it across ten files to improve a word carries regression
risk out of proportion to the gain.

Addressed at the source instead: `EQUIPMENT_MODEL_AND_STATE.md` §3.1 now opens by defining the term
— session means a protocol connection and says nothing about authorization; OPC UA has sessions,
Modbus does not; who may write is answered per protocol elsewhere — and notes that conflating the two
is what produced OD-003.

## Open items for the Phase 2 spike
Recorded as verification steps, not assumptions. Items 1 and 2 failing would change the library
choice; none of them changes the requirement.

1. The OPC UA stack's node write path (`BaseVariableState.OnWriteValue` or its equivalent in
   `1.5.378.176`) can return `Bad_OutOfRange` for a value-range violation. The OPC UA *protocol*
   defines the status code; what needs confirming is the stack's callback surface.
2. `NModbus` puts exception `0x03` **on the wire** when `InvalidModbusRequestException(3)` is thrown
   from `WritePoints`, rather than dropping the connection. Reading `ModbusSlave.ApplyRequest`
   suggests it does; that is not the same as observing it.
3. A rejected write leaves **both** setpoint registers unchanged.
4. `FC16` with quantity exactly 2 succeeds; `FC06` and every wrong quantity or start address are
   rejected deterministically with `0x02`.
5. One **23-register block read is snapshot-consistent** against the simulator's 100 ms update
   cycle — a read that straddles a tick must not return a torn sample.
6. `sourceEpochMs` decodes high-word-first across `+20/+21`, and `statusBitmap` at `+22` is covered
   by the AC-021 cross-protocol comparison.
7. The gateway's OPC UA identity genuinely cannot write, and a write function code sent to the
   Modbus read-only listener on `5020` is refused with exception `0x01` — both proven by a test
   rather than asserted (OD-003, AC-039).
8. Container image digests and transitive dependency versions are resolved and pinned per
   `TOOLCHAIN.md`'s verification procedure, with a licence scan of the full graph, and recorded.

## Sources
- OPC Foundation `UA-.NETStandard` `LICENSE.txt` — https://github.com/OPCFoundation/UA-.NETStandard/blob/master/LICENSE.txt
- `OPCFoundation.NetStandard.Opc.Ua.Server` on NuGet — https://www.nuget.org/packages/OPCFoundation.NetStandard.Opc.Ua.Server
- `FluentModbus` documentation, server request validation — https://apollo3zehn.github.io/FluentModbus/
- `NModbus` — https://github.com/NModbus/NModbus
- `NModbus.InvalidModbusRequestException` API — https://nmodbus.github.io/api/NModbus.InvalidModbusRequestException.html
