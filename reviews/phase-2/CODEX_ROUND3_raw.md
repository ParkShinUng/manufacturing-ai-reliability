# Codex verification - Phase 2, round 3

> Raw output, transcribed verbatim from the `codex exec` run of 2026-09-21 (read-only; after the
> product owner chose OD-006 option B).

Verified read-only against the uncommitted tree.

Checks run:
`dotnet test src/dotnet/Mair.sln --no-restore` passed: 193 passed, 2 skipped.  
`validate_examples`, `consistency_check`, `safety_invariants`, `ac_traceability` all passed.  
`node scripts/license-scan.mjs` could not run to completion because `dotnet list package` is blocked from reading `C:\Users\user\AppData\Roaming\NuGet\NuGet.Config` by filesystem ACLs, even when I redirected temp/DOTNET env vars.

Findings:

COD-P2-R3-001 · P2 · `src/dotnet/EdgeGateway/ModbusTelemetryClient.cs:87` · `ReadAsync` does not enforce the “one request outstanding” invariant the ADR relies on. Concurrent calls on the same client can write multiple requests and race multiple reads on one `NetworkStream`; the production poll loop currently calls it serially, but the client’s correctness argument is not protected at the API boundary. · This matters because ADR-0020’s hand-written-client amendment narrows Modbus risk specifically by claiming a single outstanding request/response; without a guard, a future caller can reintroduce framing ambiguity. · Add a `SemaphoreSlim`/interlocked in-flight guard around the whole request/response exchange, or make concurrent calls fail deterministically with a test.

COD-P2-R3-002 · P3 · `src/dotnet/EdgeGateway.Tests/ModbusFramingTests.cs:165` · The malformed-response theory does not include a nonzero/wrong MBAP protocol id case. The implementation checks protocol id at `ModbusTelemetryClient.cs:117`, so this is a test gap, not a current handling bug. · This matters because the round-3 request explicitly asks whether any ADR-0020 framing risk is untested, and “protocol id must match exactly” is part of the OD-006 change description. · Add `"protocol id"` to `MalformedResponses` and return a response with bytes 2..3 set nonzero, asserting rejection and socket close.

Round-3 checklist: FC04 framing is otherwise handled correctly, and keeping the connection after a well-framed exception PDU is sound because the stream is still aligned. The ADR amendment holds as a client-only exception to the earlier server rejection, with the invariant caveat above. COD-P2-R2-001 is fixed: server timestamps are strictly increasing per equipment and each sample reference is published once. I did not find P0/P1 regressions from rounds 1 or 2.

ACCEPT_WITH_CONDITIONS.
