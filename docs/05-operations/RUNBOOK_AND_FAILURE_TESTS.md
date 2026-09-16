# Runbook and Failure Tests

> **Executable detail lives in `docs/06-development/TEST_SPECIFICATIONS.md` §4**, which gives each
> test its setup, trigger, expected transitions, thresholds, and pass/fail assertions. v0.2 listed
> the scenarios but not the assertions, so results were not reproducible (GAP-094).
> v0.3 adds six tests that did not exist: `FAIL-SUP-001` (Supervisor death), `FAIL-SUP-002` (late
> command fencing), `FAIL-PLAT-001` (total platform stop), `FAIL-MODEL-002` (publisher death),
> `FAIL-DB-001` (database outage), `FAIL-CLOCK-001` (clock skew).

## Demo failure tests
1. `FAIL-AI-001`: terminate prediction service; verify AC-004.
2. `FAIL-OT-001`: stop the gateway-facing endpoint — OPC UA or the Modbus read listener `5020` —
   then restore; verify AC-002.
3. `FAIL-DATA-001`: inject stale telemetry; verify rejection reason.
4. `FAIL-OOD-001`: inject OOD profile; verify AC-005.
5. `FAIL-CMD-001`: duplicate command delivery; verify AC-007.
6. `FAIL-KAFKA-001`: restart broker; measure recovery and record observed loss/replay behavior.
7. `FAIL-MODEL-001 [P1]`: deploy known-bad candidate; demonstrate canary rollback.
8. `FAIL-SUP-001`: kill the Safety Supervisor; verify AC-011 (watchdog fallback within 12 s).
9. `FAIL-SUP-002`: delay a command past a watchdog fallback; verify AC-013 (`COMMAND_EPOCH_STALE`).
10. `FAIL-PLAT-001`: stop the entire platform; verify AC-012 (equipment dead-man revert).
11. `FAIL-MODEL-002`: kill `mlops-publisher`; verify AC-034 (watermark timeout → fail closed).
12. `FAIL-DB-001`: stop PostgreSQL; verify the watchdog still applies fallback.
13. `FAIL-CLOCK-001`: step a host clock; verify no stale data is accepted as fresh.

Each run must record:
- git commit;
- deployment profile;
- machine/cluster specs;
- duration;
- configuration;
- observed metrics;
- pass/fail against AC IDs.
