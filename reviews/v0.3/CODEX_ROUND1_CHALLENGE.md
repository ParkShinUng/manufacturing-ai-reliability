DEC-001
VERDICT: REJECT
CHALLENGE: Proposal creates two autonomous fallback writers: Control Service writes fallback while simulator/equipment dead-man also self-reverts, weakening ADR-0002/FR-035’s “Control Service is the sole equipment-write owner” boundary (`docs/07-adr/ADR-0002-ai-not-direct-control.md`, `docs/01-requirements/FUNCTIONAL_REQUIREMENTS.md`, `docs/02-architecture/SECURITY_BOUNDARIES.md`). It does not fence late Supervisor commands after `SUPERVISOR_SILENT`, so a delayed AI-assisted command can arrive after fallback and move the equipment back up.
SIMPLER_ALTERNATIVE: Put fallback ownership in the simulated equipment dead-man only for Supervisor/Control death, and require Control Service to continuously refresh the latest safe setpoint while healthy; audit absence via health/metrics, not a second control writer.
NEW_RISK_INTRODUCED: Split-brain between Control Service watchdog state and Supervisor intent state; also contract drift because `SAFE_FALLBACK` mode ownership is not defined in `FAILURE_MODEL.md` degraded modes.
UNHANDLED_RACE_OR_RESTART: Supervisor sends AI command at T0, network delays it; Control Service silence timeout fires at T+15s and writes 60%; delayed command arrives at T+16s with unexpired/accepted payload unless explicitly fenced.
REQUIRED_MODIFICATION: Define a single fallback authority per failure class, add a fenced control epoch/lease after watchdog fallback, reject stale Supervisor commands until Supervisor re-handshakes, and justify timeout values against documented 5s inference cadence/10s TTL in `docs/02-architecture/RUNTIME_BEHAVIOR.md`.

DEC-002
VERDICT: ACCEPT_WITH_MODIFICATION
CHALLENGE: “TTL evaluated against wall clock” depends on a clock model that the backlog itself says is missing (`GAP-060` in `reviews/v0.3/RECONCILED_GAP_ANALYSIS.md`), so it can reject valid predictions or accept stale ones under skew. It also conflicts with Kafka’s replay value in ADR-0001 unless the Supervisor’s non-replay status is explicitly scoped away from AC-003 replay tests.
SIMPLER_ALTERNATIVE: Use existing `expiresAtUtc`/command expiry semantics plus `source_prediction_at_utc` only in Control Service; Supervisor groups start at latest and replay tests use non-command projection groups.
NEW_RISK_INTRODUCED: Operational recovery burden: after a brief Supervisor restart longer than the 10s TTL, it may discard all buffered predictions and enter fallback even though Kafka recovered correctly.
UNHANDLED_RACE_OR_RESTART: Supervisor restarts for 12s, resumes at latest committed offset, consumes predictions produced during downtime, then rejects all by wall-clock TTL; if no fresh prediction arrives immediately, fallback behavior is induced by restart length rather than data validity.
REQUIRED_MODIFICATION: Add clock-source/skew rules before relying on wall-clock TTL; define Supervisor offset commit policy; define whether restart catch-up within the 10s prediction TTL is allowed or intentionally rejected.

DEC-003
VERDICT: REJECT
CHALLENGE: Per-equipment `intent_sequence` assigned by Safety Supervisor is unsafe unless the Supervisor is strictly singleton and its counters survive restart; neither condition is documented in `SYSTEM_ARCHITECTURE.md` or ADR-0009. A lost counter can make all future commands look superseded by Control Service’s persisted `last_applied_intent_sequence`.
SIMPLER_ALTERNATIVE: Let Control Service own per-equipment command ordering by comparing `source_prediction_at_utc` plus command expiry and persisting last accepted `decision_id`/prediction timestamp; avoid a distributed counter.
NEW_RISK_INTRODUCED: Hidden coupling from Control Service correctness to Supervisor counter storage/replica topology.
UNHANDLED_RACE_OR_RESTART: Supervisor applies sequence 900, crashes before persisting 901 seed, restarts at 0, and every command is rejected as `COMMAND_SUPERSEDED`; with two Supervisor replicas, both can emit sequence 901 for different predictions.
REQUIRED_MODIFICATION: Either make Safety Supervisor singleton with durable per-equipment counters and fencing, or move ordering authority to Control Service and contract it in `contracts/proto/control/v1/control.proto`.

DEC-004
VERDICT: REJECT
CHALLENGE: Defining `MANUAL_OPERATOR` now but implementing it P1 is spec bloat and creates a fifth control mode while the four existing modes still lack owner/transition/race semantics (`GAP-024`, `FAILURE_MODEL.md`). The only baseline problem is one sentence in `API_AND_COMMAND_CONTRACTS.md` allowing manual origin.
SIMPLER_ALTERNATIVE: Delete “explicit human/manual mode” from v0.3 production command origin; record manual control as Deferred/P1 with no contract surface.
NEW_RISK_INTRODUCED: A documented-but-unimplemented bypass may be mistaken as approved architecture and affects auth, audit, mode transitions, dashboard state, and STOP_REQUIRED semantics.
UNHANDLED_RACE_OR_RESTART: Manual mode active, Supervisor restarts and resumes AI decisions unless the mode is persisted and read before command emission; proposal names the invariant but not the state owner/storage.
REQUIRED_MODIFICATION: Remove manual mode from baseline contracts, or fully specify state owner, persistence, authz, transition table, recovery behavior, and AC coverage now.

DEC-005
VERDICT: ACCEPT_WITH_MODIFICATION
CHALLENGE: Hard reject is the simpler choice and aligns with MASTER_SPEC §7, but the proposal only edits one contradiction and does not reconcile the 9-gate vs 10-gate mismatch called out in `GAP-025` (`MASTER_SPEC.md` §7, `docs/04-ai/AI_SAFETY_AND_MLOPS.md` Safety gate order). It closes OOD semantics but not the gate-list authority problem.
SIMPLER_ALTERNATIVE: Make MASTER_SPEC reference one canonical gate table in `docs/04-ai/AI_SAFETY_AND_MLOPS.md`, with OOD hard reject as one row.
NEW_RISK_INTRODUCED: None, if gate lists are unified.
UNHANDLED_RACE_OR_RESTART: none
REQUIRED_MODIFICATION: Remove “reduce AI authority,” unify the normative gate list/order, and add a stable OOD rejection acceptance test mapped to AC-005.

DEC-006
VERDICT: ACCEPT_WITH_MODIFICATION
CHALLENGE: Removing request `source` is correct for authenticity, but the audit chain still needs a server-derived origin in command outcome/decision records to satisfy NFR-002 and `SECURITY_BOUNDARIES.md` audit logging. The local shared-secret fallback risks recreating the “any process can claim Supervisor” hole if it is reusable across callers.
SIMPLER_ALTERNATIVE: For local profile, bind Control Service to loopback or compose-private network and use a per-service token mounted only into Safety Supervisor; production-like profile requires mTLS.
NEW_RISK_INTRODUCED: Shared-secret sprawl and weak local parity with production auth behavior.
UNHANDLED_RACE_OR_RESTART: Certificate/token rotation during Control Service restart can reject valid Supervisor commands and trigger fallback unless rotation grace and startup policy are defined.
REQUIRED_MODIFICATION: Keep `source` out of request but add server-derived `authenticated_source` to outcome/audit contracts; define local token scope/rotation; forbid a global shared secret.

DEC-007
VERDICT: ACCEPT_WITH_MODIFICATION
CHALLENGE: Making `contracts/` authoritative is consistent with `REPOSITORY_STRUCTURE.md`, but a mandatory common envelope with `sequence` for every Kafka event imports unresolved sequence semantics from `GAP-051` into all contracts. It may overfit telemetry ordering needs onto prediction/model-deployment/fault events.
SIMPLER_ALTERNATIVE: Make `contracts/` authoritative, then define a minimal common envelope without universal `sequence`; add per-stream/per-equipment sequence only where consumers need gap/order detection.
NEW_RISK_INTRODUCED: Global-looking `sequence` fields with no reset/scope rules will create false ordering assumptions across producers.
UNHANDLED_RACE_OR_RESTART: Producer restart resets a per-process sequence to 0 and consumers treat valid new events as duplicates or gaps unless scope/epoch is explicit.
REQUIRED_MODIFICATION: Define `sequence` scope per event type, producer identity, restart behavior, and gap handling before adding it universally.

DEC-008
VERDICT: ACCEPT_WITH_MODIFICATION
CHALLENGE: Nullable raw measurements solve the JSON NaN problem, but `quality.overall` derivation requires a safety-required sensor map now only partially appears in `docs/02-architecture/EQUIPMENT_MODEL_AND_STATE.md` while the actual schema still allows arbitrary `quality.flags` strings (`contracts/jsonschema/v1/telemetry.schema.json`). The proposal does not say whether the Edge Gateway or simulator computes `overall`, even though Gateway owns normalization/quality flags in `SYSTEM_ARCHITECTURE.md`.
SIMPLER_ALTERNATIVE: Keep all raw measurement keys required, allow values to be number|null, and require Gateway to compute closed-enum per-sensor quality plus `overall` using the equipment-profile sensor map.
NEW_RISK_INTRODUCED: ML/aggregation paths may silently treat null as zero/drop rows unless derived feature aggregates are explicitly contracted.
UNHANDLED_RACE_OR_RESTART: Sensor drops out mid 60-second feature window; feature builder restarts and recomputes aggregates differently unless null-window aggregation rules are deterministic.
REQUIRED_MODIFICATION: Define quality owner, per-sensor flag shape, null aggregation rules, and contract tests for raw telemetry plus derived features.

DEC-009
VERDICT: NEEDS_HUMAN_DECISION
CHALLENGE: A 300,000 ms authorization staleness bound means a quarantined model can keep influencing equipment for up to 5 minutes during MLflow outage, despite `MODEL_QUARANTINED` being a safety gate in `MASTER_SPEC.md` §7 and ADR-0010 requiring authorized stage/cohort at decision time. That may be acceptable for a portfolio simulator, but it is a human risk-tolerance decision, not an engineering default.
SIMPLER_ALTERNATIVE: Fail closed immediately when registry/quarantine state cannot be refreshed for control-authoritative models; allow cached metadata only for non-authoritative shadow/training paths.
NEW_RISK_INTRODUCED: Hidden synchronous safety dependency on MLflow freshness, contrary to `SYSTEM_ARCHITECTURE.md` saying local safe-control must not depend synchronously on MLflow.
UNHANDLED_RACE_OR_RESTART: Model is quarantined at T0 while MLflow is unreachable from Supervisor; Supervisor continues accepting predictions until T+300s, then falls back abruptly.
REQUIRED_MODIFICATION: Human must approve maximum stale-control window; document startup policy, refresh cadence, and whether model authorization is safety-critical enough to fail closed immediately.

FINDINGS THE PROPOSALS DO NOT ADDRESS AT ALL (new P0/P1 issues you see now that you have seen the proposals)
- Fallback config remains undefined: `AI_SAFETY_AND_MLOPS.md` says fallback is “configuration-backed” per equipment class, but DEC-001 depends on that config in Control Service/equipment without schema, owner, reload, or invalid-config behavior (`GAP-029`).
- Mode ownership is still unresolved: DEC-001 and DEC-004 add more mode transitions, but no single owner/state machine for `NORMAL_RULE`, `AI_ASSISTED`, `SAFE_FALLBACK`, `STOP_REQUIRED`, and possible `MANUAL_OPERATOR` is defined (`FAILURE_MODEL.md`, `GAP-024`).
- Command outcome/equipment-state schemas are still absent even though DEC-001, DEC-003, and DEC-007 rely on them (`GAP-002`, `GAP-008`).
- Rate-of-change semantics remain unresolved: `RATE_CHANGE_LIMITED` still does not say clamp vs reject, and DEC-003 ordering does not define baseline as last commanded vs confirmed applied (`GAP-033`, `GAP-034`; `EQUIPMENT_MODEL_AND_STATE.md` says applied rate slews).
- Reason codes proposed by Claude (`SUPERVISOR_SILENT`, `COMMAND_SOURCE_STALE`, `COMMAND_SUPERSEDED`, `MANUAL_MODE_ACTIVE`, `COMMAND_SOURCE_UNAUTHORIZED`, `MODEL_AUTHORIZATION_STALE`) are not in `docs/03-contracts/REASON_CODES.md`.

WHICH DECISIONS SHOULD ESCALATE TO A HUMAN AND WHY
- DEC-001: It changes the safety/control authority model and may violate the “sole equipment-write owner” principle in FR-035/ADR-0002.
- DEC-004: Manual operation is product scope and operator liability, not just architecture cleanup.
- DEC-009: The acceptable window for a quarantined model to retain control authority during registry outage is a risk decision; 5 minutes is arbitrary without human sign-off.

Codex session ID: 01a0a26a-5fd6-71d2-a5e0-542d27a27c34
Resume in Codex: codex resume 01a0a26a-5fd6-71d2-a5e0-542d27a27c34
