# Development Protocol — Documentation First

> **v0.3: this document covers the documentation sequence. The full 20-step workflow, including the
> independent-review steps that v0.2 lacked, is in
> [`DUAL_AGENT_PROTOCOL.md`](DUAL_AGENT_PROTOCOL.md).**
> Readiness criteria are in [`DEFINITION_OF_READY.md`](DEFINITION_OF_READY.md); completion criteria
> in [`DEFINITION_OF_DONE.md`](DEFINITION_OF_DONE.md).

## Change workflow
Every feature/fix follows this exact sequence:

### Stage 0 — Change request
Create a change note containing:
- problem statement;
- affected FR/NFR/AC IDs;
- expected user/system behavior;
- architecture impact;
- contract impact;
- operational/failure impact;
- unresolved decisions.

### Stage 1 — Documentation update
Before code:
1. modify requirements if behavior changes;
2. modify architecture/failure model if boundaries or degradation change;
3. create/update ADR for non-trivial decisions;
4. modify contracts before producer/consumer changes;
5. update acceptance criteria and tests;
6. update roadmap/backlog.

### Stage 2 — Documentation consistency review
Verify:
- every P0 behavior has an FR/NFR ID;
- every FR has testable AC or an explicit reason;
- no event/API exists without version/ownership;
- no new dependency lacks ADR/rationale;
- failure behavior is defined;
- security and observability impacts are addressed.

### Stage 3 — Implementation plan
Agent/human writes a plan referencing exact document IDs and files. Plan must state what will **not** change.

### Stage 4 — Implementation
Implement only approved behavior. Missing behavior is a documentation blocker, not an invitation to guess.

### Stage 5 — Verification
Run unit/integration/contract/failure tests mapped to AC IDs. Record results.

### Stage 6 — Documentation reconciliation
If implementation exposed a necessary design change, update docs/ADR first, then adjust code. Never leave intentional drift.

## Independent review (v0.3)

Stages 0-6 below are Claude's. Three further stages are mandatory for any change touching authority,
safety, messaging semantics, persistence, security, or failure recovery:

| Stage | Actor | Output |
|---|---|---|
| **3b** | Codex | adversarial challenge of the proposal, **before** implementation |
| **5b** | Codex | independent read-only verification of the finished work |
| **5c** | Codex | re-verification after Claude resolves the findings |

Codex recommends; **Claude implements**. A reviewer that edits the work is reviewing itself on the
next pass. Full role definitions and the mandatory-participation list:
[`DUAL_AGENT_PROTOCOL.md`](DUAL_AGENT_PROTOCOL.md).

## Pull request template requirements
- Requirements: FR/NFR IDs
- Acceptance criteria: AC IDs
- ADRs: IDs
- Contract changes: yes/no + versions
- Failure impact
- Observability impact
- Tests run
- Documentation updated: yes/no
- Codex challenge: run / not required (+ link to `reviews/`)
- Codex verification: P0 count, P1 count
- Definition of Ready: satisfied yes/no
