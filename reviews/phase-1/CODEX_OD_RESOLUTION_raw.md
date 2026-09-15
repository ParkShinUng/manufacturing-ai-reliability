# Codex verification - OD-001 / OD-002 resolution change set

> Raw output from the `codex exec` run of 2026-09-15, verbatim. Required by
> `DUAL_AGENT_PROTOCOL.md` section 2: the change alters equipment-safety constants.
>
> Codex ran the suite and the three contract scripts itself.
>
> The three findings below were accepted and fixed: `fault_injections_total` now carries the
> documented `profile` label, the stale over-temperature test comment was corrected, and the Phase 1
> changelog entry now records that OD-001 superseded its profile count.

**Findings**

P2 - Observability counter shape mismatch: docs specify `fault_injections_total{profile}` in [OBSERVABILITY_AND_SLO.md](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/docs/05-operations/OBSERVABILITY_AND_SLO.md:100) and [EQUIPMENT_SIMULATOR.md](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/docs/11-service-design/EQUIPMENT_SIMULATOR.md:100), but implementation exposes only scalar `FaultInjectionsTotal` at [EquipmentSimulation.cs](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/src/dotnet/EquipmentSimulator/EquipmentSimulation.cs:130), and the test asserts only the scalar at [ActuationTests.cs](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/src/dotnet/EquipmentSimulator.Tests/ActuationTests.cs:197). This does not create an unbounded label, but it also does not implement the documented bounded `profile` label.

P3 - Profile count is still inconsistent in changelog prose. Current normative docs/tests say 11 profiles, but [CHANGELOG.md](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/CHANGELOG.md:99) still says “all 10 fault profiles.” The older v0.3 historical entry at line 146 also says 10; that may be historical, but the Phase 1 implementation summary is stale.

P3 - Stale over-temperature test comments still describe the old `c=0.9` / 120.46 C margin at [ProtectiveConditionTests.cs](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/src/dotnet/EquipmentSimulator.Tests/ProtectiveConditionTests.cs:66). The assertion is not weakened, but the comment contradicts OD-002 and the new `c=0.95` model.

**Checks**

No raised constant deterministically pushes a channel outside section 1.2 valid ranges under permitted model combinations. The implementation permits one active `FaultProfile`; `OVERLOAD` does not combine with bearing health, and `COOLING_DEGRADATION` does not combine with bearing health. Worst cases remain under documented ranges: 33.6 A < 40 A, 117.6 N.m < 150 N.m, 133.3 C < 160 C, 28.6 mm/s < 50 mm/s. Gaussian noise is still theoretically unbounded, but that predates this change and upper excursions are intentionally flagged, not clamped.

The `DEGRADED` to `FAULT` ladder still has usable separation. Vibration has an explicit test requiring `DEGRADED` before `FAULT` at [FaultProfileSignatureTests.cs](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/src/dotnet/EquipmentSimulator.Tests/FaultProfileSignatureTests.cs:101). Temperature still has 95 C vs 120 C thresholds.

`DRIVE_STUCK` is correctly prevented from surviving a protective trip: `FAULT` overrides the stuck condition in [EquipmentSimulation.cs](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/src/dotnet/EquipmentSimulator/EquipmentSimulation.cs:399), and the regression test proves applied rate returns to zero.

Verification run: `dotnet test src/dotnet/Mair.sln --no-restore` passed 62/62; all three node contract scripts passed.

VERDICT: PASS WITH P2/P3 DOCUMENTATION/OBSERVABILITY FINDINGS; no P0/P1 safety blocker found.
