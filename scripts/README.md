# Operational Scripts

Reproducibility tooling. **Every performance claim in this repository must be produced by a script
here, never by hand** (NFR-005, AC-042) — a number typed into a document is an assertion, and this
project does not accept assertions as evidence.

---

## Verification you can run today

These need no application code and no infrastructure. They are the reason the specification phase
could be checked at all.

```bash
node tests/contract/validate_examples.mjs   # documented examples vs the JSON Schemas
node tests/contract/consistency_check.mjs   # cross-document contradiction checks
node tests/contract/safety_invariants.mjs   # SC-01..SC-10 safety configuration invariants
```

Phase 1 adds the simulator suite:

```bash
dotnet test src/dotnet/Mair.sln
```

---

## To be added, by phase

| Script | Phase | Purpose |
|---|---|---|
| environment capture | 1–2 | git commit, machine spec, deployment profile, configuration — stamped into every report so a number can be traced back to what produced it |
| fault-injection drivers | 2+ | the 13 failure tests in `TEST_SPECIFICATIONS.md` §4 |
| load-profile drivers | 3+ | 20 and 250 equipment (LOAD-001, LOAD-002) |
| DLQ redrive | 3+ | **operator-initiated only, never automatic** — an automatic redrive turns a poison message into an infinite loop |

---

## Retired

### `finalize-rename.ps1` — kept as a record, not to be run

Renamed `Portfolio\aegis-factory` to `Portfolio\manufacturing-ai-reliability` while preserving the
Claude Code session. **It has served its purpose and is retained only as a record**; the rename
completed on 2026-09-15 and the junction it created is gone.

It is kept because of what it documents rather than what it does. Renaming the folder alone would
have lost the conversation: Claude Code keys per-project state by working-directory path, and three
separate places are keyed that way — the transcript directory under `~/.claude/projects/`, the
`"cwd"` field inside every line of each `.jsonl`, and the `projects[...]` entry in `~/.claude.json`
that holds trust approval and tool permissions. Missing the third alone re-prompts the trust dialog
and forgets every granted permission.

The script also found a real bug before its first run, which is the part worth keeping:
PowerShell's `-replace` is a **regex** operator, so `'\\'` → `'\\\\'` produced four backslashes and
matched nothing. Without the test the script would have reported success while changing nothing, and
the conversation would have been lost at the next launch. The fix is `[string]::Replace`, which is
literal.

Do not run it. Its path constants refer to a directory layout that no longer exists.
