# Codex independent verification - Phase 1 (commit 8d24190)

> Raw output, transcribed verbatim from the `codex exec` run of 2026-09-15. The only alteration is
> the separator character: the Windows console rendered Codex's `·` as mojibake, and it is restored
> here. No wording, severity, or ordering has been changed.
>
> Request: [`CLAUDE_VERIFICATION_REQUEST.md`](CLAUDE_VERIFICATION_REQUEST.md).
> Claude's classification of every finding: [`CLAUDE_ROUND2_CLASSIFICATION.md`](CLAUDE_ROUND2_CLASSIFICATION.md).
>
> Codex could not run `dotnet test` itself: MSBuild could not create a temp directory under the
> read-only sandbox. That is a sandbox limitation, not a code failure, and Codex reported it as such
> rather than inferring a result.

COD-VFY-001 쨌 P0 쨌 src/dotnet/EquipmentSimulator/EquipmentSimulation.cs:159 쨌 `OperatorReset()` only checks injected conditions, over-temperature, and `STOP_REQUIRED`; it ignores live `SafetySensorBad` and non-injected over-current conditions that `UpdateAndCollectProtectiveConditions()` uses to enter `FAULT` 쨌 an operator can reset a machine faulted by a still-missing safety-required sensor, returning it to `IDLE` and accepting commands for up to the 2 s bad-quality sustain window before it re-faults 쨌 make reset use the same sensed protective-condition evaluation as the tick path, or cache the last reading/quality state and refuse reset while any §3.4 condition is still live.

COD-VFY-002 쨌 P1 쨌 src/dotnet/EquipmentSimulator/EquipmentSimulation.cs:192 쨌 `FaultProfile.CommLoss` does not itself stop the endpoint; `_commLost` is changed only by the demo-only `InjectCommLoss()` method, and the AC-018 test constructs `FaultProfile.CommLoss` then manually injects comm loss 쨌 the test proves the injection API, not that the documented `COMM_LOSS` fault profile produces its signature 쨌 implement configured `CommLoss` behavior from the active profile, and change the test so the profile alone causes null samples while physics continues.

COD-VFY-003 쨌 P1 쨌 src/dotnet/EquipmentSimulator/EquipmentSimulation.cs:270 쨌 the `sourceEpochMs` wrap reset is mathematically wrong for 100 ms ticks: `_elapsedMs` will step from `4294967200` to `4294967300`, so `_elapsedMs % 2^32 == 0` is false at the actual uint wrap; sequence will not reset until 25 wraps later 쨌 consumers will see `sourceEpochMs` wrap to a small value while `sequence` continues, contradicting §11’s “reset sequence simultaneously” rule 쨌 detect wrap by comparing the previous and next `uint` epoch values, or compute sequence reset before emitting the first post-wrap sample.

COD-VFY-004 쨌 P1 쨌 src/dotnet/EquipmentSimulator/EquipmentSimulation.cs:299 쨌 the rate-deviation degradation condition is unreachable because `_expectedPct` is assigned `_appliedPct` every tick, so `Math.Abs(_appliedPct - _expectedPct) > 10` can never be true 쨌 §3.3 documents drive tracking failure as a degradation condition, but the implementation silently disables it and no test can fail if it stays disabled 쨌 maintain an independent slew-limited expected rate from the commanded setpoint and add a drive-lag/stuck-drive test path that proves T7 fires after `slew_grace`.

COD-VFY-005 쨌 P1 쨌 src/dotnet/EquipmentSimulator/EquipmentSimulation.cs:493 쨌 protective conditions are collected in `STOPPING` but deliberately cannot latch `FAULT` there; §3.4 says any protective condition is sufficient for `→ FAULT`, while T9’s table omits `STOPPING` 쨌 an over-temperature/over-vibration condition during a stop can be unaudited and avoid the operator-reset requirement if it clears before the drive reaches `IDLE` 쨌 resolve the spec contradiction explicitly; safest implementation is to let `STOPPING` enter `FAULT` for §3.4 protective conditions while preserving stop-timeout behavior.

COD-VFY-006 쨌 P1 쨌 src/dotnet/EquipmentSimulator.Tests/ProtectiveConditionTests.cs:51 쨌 the “entire platform stopped” proof checks only static non-framework assembly references 쨌 that proves a weaker property than AC-020; it does not by itself prove protective trips and dead-man behavior continue without Kafka, Control Service, Supervisor, wall-clock services, or protocol hosts running 쨌 keep the structural check, but pair it with behavioral tests that exercise each protective trip and the dead-man using only `EquipmentSimulation.Tick()` and no external process/service fixtures.

VERDICT 쨌 REJECT 쨌 Phase 1 has real safety/correctness gaps: reset can clear a live safety fault, comm-loss as a fault profile is not implemented, source epoch wrap is wrong, and one documented degradation condition is unreachable. I attempted `dotnet test src/dotnet/Mair.sln --no-restore`, but MSBuild could not create a temp directory under the read-only sandbox, so I could not rerun the suite here.
