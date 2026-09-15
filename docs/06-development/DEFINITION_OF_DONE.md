# Definition of Done

> Start-of-work criteria are in [`DEFINITION_OF_READY.md`](DEFINITION_OF_READY.md). A work item that
> was never *ready* cannot become *done* by passing tests.

A work item is done only when:
- docs were updated first;
- linked FR/NFR/AC IDs exist;
- relevant ADR is accepted;
- public contracts and examples are current;
- unit/integration tests pass;
- failure behavior is tested when applicable;
- metrics/logs/traces are added for operationally meaningful behavior;
- no secrets are committed;
- README/runbook changes are complete;
- no undocumented TODO affects behavior;
- **Codex independent verification has run, with P0 = 0 and P1 = 0** (for changes touching authority,
  safety, messaging semantics, persistence, security, or failure recovery);
- **every Codex finding is classified**, none silently dropped;
- the Decision Record and ADR match what was actually built, not what was proposed;
- human approval obtained where `HUMAN_DECISION_REQUIRED` applies.
