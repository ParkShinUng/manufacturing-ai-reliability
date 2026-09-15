# Operational Scripts

> **The implementation gate is OPEN** (v0.3 approved, Phase 1 authorized) but implementation has not
> started. Scripts that drive the application belong here once Phase 1 begins; nothing in this
> directory should run application code before then.

---

## finalize-rename.ps1 — renames the folder **and keeps this conversation**

### Why the folder has two paths right now

| Path | What it is |
|---|---|
| `Portfolio\aegis-factory` | the real directory |
| `Portfolio\manufacturing-ai-reliability` | a **directory junction** pointing at it |

A running Claude Code session holds its working directory open, and Windows will not rename a
directory that any process has open. This was diagnosed, not assumed: the holder is the `claude`
process itself, which resets the shell's cwd after every tool call. The junction makes the new path
usable immediately without ending the session — both paths are the same files.

### The part that actually matters: session continuity

**Renaming the folder alone would lose the conversation.** Claude Code keys per-project state by the
working-directory path, so a renamed folder looks like a brand-new project with no history. Three
things are path-keyed:

| # | Location | Contents |
|---|---|---|
| 1 | `~/.claude/projects/<sanitized-path>/` | transcripts (`.jsonl`), sidecar dir, `memory/` |
| 2 | the `"cwd"` field **inside** each `.jsonl` | 1,224 of 1,960 lines in this session |
| 3 | `~/.claude.json` → `projects["<path>"]` | trust approval, `allowedTools`, MCP config — 28 fields |

Miss #3 and the new folder re-prompts the trust dialog and forgets every tool permission.

The sanitized key is the absolute path with `:` and `\` replaced by `-`. Verified against an existing
project whose folder name already contains hyphens, so hyphens in the name are preserved:

```
C:\Users\user\Desktop\workspace\Portfolio\manufacturing-ai-reliability
    → C--Users-user-Desktop-workspace-Portfolio-manufacturing-ai-reliability
```

`finalize-rename.ps1` migrates all three, then renames the folder.

### Usage

```powershell
# 1. Close Claude Code. The script refuses to run while it is open.
# 2. From a terminal OUTSIDE both folders:

powershell -ExecutionPolicy Bypass -File .\scripts\finalize-rename.ps1 -WhatIf   # dry run
powershell -ExecutionPolicy Bypass -File .\scripts\finalize-rename.ps1

# 3. Continue the conversation:
cd C:\Users\user\Desktop\workspace\Portfolio\manufacturing-ai-reliability
claude --continue
```

`claude --continue` resumes the most recent session; `claude --resume` offers a picker.

### Safety properties

- **refuses to run while Claude Code is open** — the transcript is being appended to live, and
  migrating a file mid-write would corrupt it
- **backs up** `~/.claude/projects/<key>/` and `.claude.json` before touching anything
- aborts if the target path is a real folder rather than the expected junction — two real folders
  would mean divergent copies, which is not something a script should resolve on its own
- removes only the junction, never its target, and verifies the real folder survived that step
- **restores the junction** if the rename fails, rather than leaving a half-renamed workspace
- explicit UTF-8 on every read and write — the transcript contains non-ASCII text that the
  PowerShell 5.1 default encoding would silently corrupt
- leaves a transcript **untouched** if it produced 0 replacements, since that indicates wrong path
  constants rather than finished work
- re-runs the three verification suites at the new path

### Verified before first use

The rewrite logic was tested against a byte-exact 300-line copy of the real transcript:

```
lines containing old path : 191
replacements performed    : 191
old path remaining        : 0
JSON parse after          : 300/300 ok
line count preserved      : PASS
Hangul intact             : PASS
```

That test found a real bug first. `-replace` is a **regex** operator, so `'\\'` → `'\\\\'` produced
*four* backslashes instead of two and matched nothing. Without the test the script would have
reported success while silently changing nothing, and the conversation would have been lost on the
next launch. The fix is `[string]::Replace`, which is literal.

---

## Scripts to be added with Phase 1 onward

Reproducibility tooling. Every performance claim must be produced by a script here, never by hand
(NFR-005, AC-042).

- environment capture (git commit, machine spec, deployment profile, configuration)
- fault-injection drivers for the 13 failure tests in `TEST_SPECIFICATIONS.md` §4
- load-profile drivers (20 and 250 equipment)
- DLQ redrive — operator-initiated only, never automatic
