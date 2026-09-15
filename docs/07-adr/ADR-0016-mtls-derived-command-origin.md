# ADR-0016 — Command origin is authenticated, never self-asserted
Status: Accepted (v0.3, dual-agent reviewed)
Decision Record: [DEC-006-command-authenticity](../09-decisions/DEC-006-command-authenticity.md)

## Context
`ApplyCommandRequest.source` was a string the caller set itself, yet the contract relied on it to restrict the production command path to the Safety Supervisor. Any process able to reach the Control Service gRPC port could claim to be the Supervisor, defeating ADR-0002 - the property the architecture exists to guarantee.

## Decision drivers
- FR-030/FR-035 and ADR-0002 require a single, verifiable command origin.
- NFR-002 requires the audit chain to retain an origin.
- NFR-006 requires least-privilege service authentication.

## Considered options
1. Keep `source`, validate against an allowlist.
2. API keys per service.
3. mTLS with server-derived origin.

## Decision
Use mTLS workload identity between Safety Supervisor and Control Service. Remove `source` from the request entirely; derive origin server-side from the verified peer certificate and return it as `authenticatedSource`, which is also a required field on the control-outcome audit record. Control Service holds an authorization map of identity to permitted command types and equipment scope.

## Rationale
Option 3. Option 1 is not authentication: an allowlist of *claimed* names still trusts the claim. Option 2 provides no workload identity and rotates awkwardly. Codex additionally required that removing `source` must not break the audit chain, which is why `authenticatedSource` appears on the response and the outcome record.

## Trade-offs
Certificate management in the production-like profile. The local profile uses a per-service token on a compose-private network, logged as a warning at startup; a global shared secret is forbidden and mTLS is required in the production-like profile.

## Reliability impact
Credential rotation uses a 300 s overlapping-validity window and is forbidden during an active `STOP_REQUIRED`, so rotation cannot induce a spurious fallback.

## Failure impact
F24 and the security rows of `FAILURE_MODEL.md`.

## Operational impact
Certificate issuance and rotation become an operational duty with a documented grace window.

## Security impact
Closes a control-authority bypass. Origin can no longer be forged by any process with network reach.

## Alternatives rejected
Allowlisting a self-asserted string was rejected because it is indistinguishable from no control at all.
