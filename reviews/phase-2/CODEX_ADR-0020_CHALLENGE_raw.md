# Codex challenge - ADR-0020 (OPC UA / Modbus TCP libraries)

> Raw output from the `codex exec` run of 2026-09-16, verbatim. Required by
> `DUAL_AGENT_PROTOCOL.md` section 2: the decision is both a protocol choice and a major dependency.
> Codex had network access and re-verified the ADR's factual claims rather than trusting them.
>
> ADR: [`ADR-0020`](../../docs/07-adr/ADR-0020-ot-protocol-libraries.md).
>
> All findings were accepted. The two P1s turned out to be defects in `OT_PROTOCOL_MAPPING.md`
> itself rather than in the ADR; they are corrected there, and the Modbus enforcement gap is now
> `OPEN_DECISIONS.md` OD-003.

**Verdict: ACCEPT_WITH_CHANGES**

The core library choice is defensible: OPC Foundation UA-.NETStandard + NModbus is the better fit than FluentModbus for the stated requirements. But ADR-0020 should not be accepted as-is because it misses two design/contract issues that matter before Phase 2 implementation.

**Findings**

P1 — Modbus FC06 conflicts with the 32-bit setpoint contract  
`OT_PROTOCOL_MAPPING.md` defines `operationRateSetpointPct` as a 32-bit value over two holding registers, but also says function code `6/16`. FC06 writes exactly one 16-bit register, so it cannot update the setpoint atomically. The ADR must decide one of these before implementation: reject FC06 for this address and require FC16 with exactly two registers, or specify a two-step semantics with buffering/fencing. NModbus can enforce exact address/quantity in `WritePoints`; FluentModbus can reject wrong address/quantity too, but still cannot validate value. This is a contract gap, not a library flaw.

P1 — The ADR repeats a server-side read-only gateway claim that Modbus TCP cannot satisfy by itself  
`EQUIPMENT_SIMULATOR.md` §16 and `OT_PROTOCOL_MAPPING.md` say the gateway OT session is provisioned without write permission. OPC UA can plausibly enforce this through sessions/users/roles, but Modbus TCP has no native authenticated session or per-client write permission. NModbus does not solve that. ADR-0020 needs to state the actual enforcement boundary for Modbus: separate listener, network policy, client certificate tunnel, allowlisted Control Service source, or a documented exception to the “server-enforced” claim.

P2 — OPC package naming is too broad and may pull unnecessary dependencies  
The ADR decision names `OPCFoundation.NetStandard.Opc.Ua`, but NuGet says that package has no supported framework assets and is a meta package pulling client, server, configuration, GDS common packages, types, etc. The ADR should name the exact split packages intended for simulator and gateway, probably `...Server`, `...Client`, `...Configuration`, and their required core/security/type dependencies, not the all-in meta package. NuGet’s Server package does explicitly include `net10.0` assets/dependencies, so the target claim is fine for the split package.

P2 — Licence claim should be narrowed  
The OPC Foundation repo now carries the OPC Foundation MIT License, and package metadata uses a license file; I found no current membership condition in the package facts. But “single MIT anywhere in the dependency graph” is over-precise unless backed by a generated lockfile/license scan. The graph includes Microsoft/Newtonsoft dependencies with their own licences. Change the claim to “no copyleft or membership-gated licence identified; verify with package lock/license scan before pinning.”

P3 — Hand-rolled Modbus rejection is directionally right but argued with the wrong evidence  
Rejecting a hand-rolled Modbus server is reasonable: network framing, transaction IDs, exception responses, concurrent connections, and reconnect behavior are different risk from the repo’s hand-rolled deterministic PRNG. But the ADR says AC-021 catches the class of mistake a hand-rolled implementation makes. AC-021 mainly catches scaling/word-order disagreement, not transaction ID handling, partial frames, exception framing, or concurrency. Tighten that rationale.

**Fact Check**

OPC UA licence: mostly confirmed. The current UA-.NETStandard license is OPC Foundation MIT, and the repo packaging comments say packages ship that license file. The old dual RCL/GPL material still exists in stale/secondary places, so the ADR is right to call that history out. Source: OPC Foundation license file and packaging metadata on GitHub/NuGet.

OPC UA `net10.0`: confirmed for the chosen split packages. NuGet shows `OPCFoundation.NetStandard.Opc.Ua.Server` 1.5.378.176 includes `net10.0`; Core/Client pages also show net10 compatibility/assets. Source: NuGet Server/Core pages.

NModbus licence and behavior: confirmed. NModbus is MIT, `3.0.83` targets `net6.0`/`netstandard`/`net46`, and its `IPointSource<T>.WritePoints(startAddress, points)` receives values. `ModbusSlave.ApplyRequest` catches `InvalidModbusRequestException` and returns `SlaveExceptionResponse` using the provided exception code, so throwing code `3` should become Modbus exception `0x03`; still worth an on-wire test. Sources: NModbus NuGet, license, `IPointSource`, `InvalidModbusRequestException`, `ModbusSlave`.

FluentModbus disqualification: accurate. Current docs expose request validation by unit/function/address/quantity, not value; events fire after writes. There is even an open FluentModbus issue requesting that write values be passed to the validator so callers can return Illegal Data Value. That supports the ADR’s conclusion.

**Other Missing Items**

ADR-0020 should add explicit Phase 2 spike tests for:

- FC16 exact two-register write succeeds; FC06/partial writes are rejected deterministically.
- Out-of-range setpoint returns Modbus exception `0x03` on the wire and does not mutate either register.
- Gateway identity cannot write over OPC UA, and the actual Modbus-side enforcement mechanism is proven.
- One 23-register read is snapshot-consistent across the simulator’s 100 ms update cycle.
- `sourceEpochMs` high-word-first decoding and `statusBitmap` at `+22` are covered in AC-021 tests.

Sources used: OPC Foundation UA-.NETStandard license and NuGet metadata, NModbus NuGet/GitHub/API docs, FluentModbus docs/issues, and the specified local repository documents.
