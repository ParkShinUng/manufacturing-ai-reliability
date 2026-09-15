# DEC-006 — Command-origin authenticity

## Status
CONVERGED — Human review not required

## Problem
`ApplyCommandRequest.source` was a **string the caller sets itself**, while
`API_AND_COMMAND_CONTRACTS.md` relied on it: "Only `SAFETY_SUPERVISOR` … may originate a production
command path." Any process able to reach the Control Service gRPC port could claim to be the
Supervisor. This defeated ADR-0002 entirely — the property the whole architecture exists to
guarantee was enforced by an unverified string.

## Relevant requirements
FR-030, FR-035, NFR-002, NFR-006. Gaps GAP-028, GAP-081.

## Related ADRs
ADR-0002, ADR-0009. New: ADR-0016.

## Claude initial proposal (Round 1)
mTLS; `source` removed from the request and derived server-side from the verified peer identity;
authorization map identity → permitted command types; shared-secret header allowed in the local
profile.

## Codex challenge (Round 1) — ACCEPT_WITH_MODIFICATION
1. Removing `source` is correct for authenticity, but the **audit chain still needs a server-derived
   origin** in outcome/decision records to satisfy NFR-002 and the `SECURITY_BOUNDARIES.md` audit
   obligation.
2. "The local shared-secret fallback risks recreating the 'any process can claim Supervisor' hole if
   it is reusable across callers." Simpler: bind to a compose-private network with a per-service
   token mounted only into the Supervisor.
3. **Rotation race**: certificate/token rotation during a Control Service restart rejects valid
   commands and triggers fallback.

## Claude response (Round 2)
**Classification: ACCEPTED on all three.**
1. `authenticatedSource` added to `ApplyCommandResponse` and to
   `control-outcome.schema.json` as a required field, server-derived and never echoed from the
   request. Removing an identity field without adding it back to the audit record would have broken
   traceability while fixing authenticity.
2. Per-service token mounted only into the Supervisor; compose-private network; **a global shared
   secret is forbidden**; the substitution is logged as a warning at startup and is forbidden in the
   production-like profile.
3. Overlapping-validity window: both old and new credentials accepted for a
   `credential_rotation_grace` of 300 s, and rotation is **forbidden during an active
   `STOP_REQUIRED`**.

## Codex re-evaluation (Round 2) — STILL_OPEN
"The proto still carries self-asserted `source`; no server-derived `authenticatedSource` in the
outcome contract."

## Claude resolution (Round 3) — ACCEPTED
Executed. `control.proto` no longer has a `source` field at all and carries an explicit comment
recording why. `ApplyCommandResponse.authenticated_source` and
`control-outcome.schema.json.authenticatedSource` are both present and required.

## Alternatives considered
| Option | Verdict |
|---|---|
| Keep `source`, validate against an allowlist | rejected — still self-asserted; an allowlist of claimed names is not authentication |
| API keys | rejected — no workload identity, awkward rotation |
| **mTLS + server-derived origin** | **adopted** |

## Evidence
A self-asserted origin field is not an access control. The v0.2 contract's own rule sentence depended
on it, which is what made this a control-authority bypass rather than a cosmetic issue.

## Trade-offs
Certificate management in the production-like profile; a documented, warned, locally-scoped exception
for local development.

## Final decision
mTLS workload identity; `source` removed from the request; `authenticatedSource` server-derived and
carried into the audit record.

## Consensus
Claude: **ACCEPT** · Codex: **ACCEPT** (Round 3)

## Human review
NOT_REQUIRED.

## Related ADR
ADR-0016.
