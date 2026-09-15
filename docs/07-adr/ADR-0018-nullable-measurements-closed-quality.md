# ADR-0018 — Nullable measurements with a closed quality vocabulary
Status: Accepted (v0.3, dual-agent reviewed)
Decision Record: [DEC-008-sensor-representation](../09-decisions/DEC-008-sensor-representation.md)

## Context
All seven measurements were required with `additionalProperties:false`, and JSON has no NaN, so a dead sensor could not be encoded at all - a producer had to fabricate a value or emit an invalid event. `quality.flags` was an unconstrained string array, leaving the `SENSOR_QUALITY_BAD` gate with no decidable input domain and unbounded metric cardinality.

## Decision drivers
- FR-006 and FR-031: telemetry must carry quality, and gates must consume it.
- A safety gate needs a decidable input domain.
- Metric labels must stay low-cardinality.
- Feature aggregation must be restart-invariant or it manufactures training-serving skew.

## Considered options
1. Sentinel value such as -999.
2. Omit the key when a sensor is dead.
3. A separate sensor-health event.
4. Nullable value with a mandatory flag and derived `overall`.

## Decision
Measurement keys remain required; values become `number|null`, where `null` means no trustworthy value exists and must be accompanied by a quality flag. Substituting a synthetic, zero, or last-known value is forbidden. `quality.flags` becomes a closed enum carried in the schema. `quality.overall` is derived by the Edge Gateway from the safety-required/advisory sensor map. Null aggregation in feature windows is deterministic: nulls excluded, `validSampleRatio` computed against the *expected* sample count, and no prediction emitted below 0.8 on any safety-required channel.

## Rationale
Option 4. A sentinel is a fabricated reading that range checks may pass. Omitting keys destabilises the consumer shape. A separate event splits an atomic sample in two. Codex additionally required naming the owner of `overall` (the Edge Gateway, since it alone holds both protocol quality and the sensor map) and contracting the null-aggregation rules, which is what prevents a feature-builder restart from recomputing a window differently.

## Trade-offs
Consumers must handle nulls explicitly - which is the intent: an explicit null is safer than a silent fabrication.

## Reliability impact
A failed sensor can now be represented honestly, so the safety gates receive the truth rather than a plausible lie.

## Failure impact
F20, F21 in `FAILURE_MODEL.md`.

## Operational impact
`quality_flags_total{flag}` is bounded and usable as a metric label; the detail field is explicitly excluded from labels.

## Security impact
Prevents a data-integrity failure mode in which fabricated readings pass safety gates.

## Alternatives rejected
Sentinel values were rejected precisely because they are indistinguishable from real readings to a range check.
