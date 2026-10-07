# Codex challenge of OD-030

> Raw output, verbatim, `codex exec -s read-only`, 2026-10-07. Verdict: **SOUND_WITH_CONDITIONS** - A,
> but as a breaking change (v2), not a v1 correction.

Verdict: `SOUND_WITH_CONDITIONS`

Recommended option: `A`, amended: gateway-derived discriminator is the right mechanism, but not as a `v1` semantic change.

**Conditions**
1. Treat the duplicate-identity change as breaking: publish it as `factory.telemetry.v2` / `telemetry.schema.json` v2, or keep `v1` identity unchanged.
2. In v2, make the discriminator required, not optional-for-consumers: identity = `(equipmentId, gatewayEpoch, sourceBoot, sequence)`.
3. Define first observed epoch precisely: at gateway start, `sourceBoot = 0` per equipment; increment only after a paired `sequence` + `sourceEpochMs` backward move.
4. Include epoch wrap in the same increment rule, or rename `sourceBoot` to something like `sourceEpochOrdinal`; 2^32 ms wrap is not literally a boot.
5. State the accepted gateway-restart limitation: re-reading the same equipment sample after gateway restart gets a new identity because `gatewayEpoch` changed.
6. Add tests for OPC UA and Modbus restart, epoch wrap, unpaired reset still `SEQUENCE_GAP`, and Phase 5 dedupe across a restart window.

**Reasoning**
`factory.telemetry.v1` currently defines duplicate identity as `(equipmentId, sequence)`, while `sequence` resets on restart.  
`EVENT_CONTRACTS.md` says optional fields are additive, but semantic changes require a new major version.  
Option A’s mechanism matches OD-008’s gateway-epoch precedent and avoids OT mapping churn.  
The gateway can only name epochs it observes; on first boot observed it cannot reconstruct prior source boots.  
Operations’ OD-015 projection does not need the new identity; it chooses a per-second winner by `(eventTimeUtc, sequence)`.  
Phase 5 dedupe does need the corrected identity because OD-028 explicitly depends on it.

**Other Findings**
`P5-OD30-001`: OD-030 misclassifies the identity change as a `v1` additive correction; it is a semantic change.

`P5-OD30-002`: `sourceBoot` is semantically leaky because the same rule must cover the 49.7-day `sourceEpochMs` wrap; use a neutral name or document the fiction.

`P5-OD30-003`: Do not use the operations projection as evidence that telemetry dedupe is fixed; `TelemetryAsync` does not dedupe by topic identity and should remain scoped to OD-015’s latest-reading rule.
