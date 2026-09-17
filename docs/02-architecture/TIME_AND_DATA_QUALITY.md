# Time Semantics and Data Quality

> Closes: GAP-060 (clock model), GAP-061 (quality vocabulary), GAP-062 (dead sensor
> representation), GAP-063 (timestamp reversal), GAP-064 (required sensor mapping).
> Depends on: DEC-008.
> **Prerequisite of DEC-002** — the wall-clock TTL rule is only sound given the skew budget defined
> here. Codex correctly identified this ordering dependency in Round 1.
> Status: v0.3 normative.

## 1. The five timestamps

v0.2 used `eventTimeUtc` and `ingestTimeUtc` without saying whose clock produced them. Under
Kubernetes (Phase 10) that ambiguity silently converts every freshness gate into a false reject or,
worse, a false accept.

| Timestamp | Produced by | Clock | Meaning |
|---|---|---|---|
| `eventTimeUtc` | equipment (OPC UA SourceTimestamp) or gateway (Modbus) | equipment / gateway | when the measurement was taken |
| `ingestTimeUtc` | edge-gateway | gateway | when the gateway received it |
| `predictedAtUtc` | prediction-service | prediction host | when inference completed |
| `decidedAtUtc` | safety-supervisor | supervisor host | when the gate evaluation completed |
| `appliedAtUtc` | control-service | control host | when the equipment write returned |

For **Modbus** equipment there is no source timestamp, so `eventTimeUtc = ingestTimeUtc` and
`TIMESTAMP_SYNTHESISED` is always raised (`OT_PROTOCOL_MAPPING.md` §2.6). Consequence, stated plainly
because it affects what the freshness gate actually measures: for Modbus equipment, telemetry
freshness measures **gateway-to-consumer** latency, not **sensor-to-consumer** latency.

## 2. Clock model

| Rule | Value |
|---|---|
| Internal representation | UTC, ISO-8601 with explicit `Z`, millisecond precision |
| Clock discipline | NTP on every host; **an operational precondition, not an assumption** |
| Max tolerated skew between platform hosts | **±250 ms** (`clock_skew_budget_ms`) |
| Skew detection | each service compares its clock to the Kafka broker timestamp on consumed records; deviation > budget raises `clock_skew_seconds` and an alert |
| Interval measurement | **monotonic clock only** (`Stopwatch` / `time.monotonic`) — never wall-clock subtraction |
| Wall-clock use | only for absolute freshness/TTL comparisons, never for durations |

### Why ±250 ms
The tightest wall-clock-sensitive gate is telemetry freshness at 2 s. A ±250 ms budget is 12.5 % of
that gate, so skew alone cannot flip a freshness decision that is not already marginal. It is also
comfortably achievable with NTP on a LAN or within a cluster.

### Skew budget is applied, not just documented
Every freshness comparison is evaluated as:

```text
age = now_wallclock − timestamp
stale  if  age > threshold + clock_skew_budget_ms
```

The budget is added on the **permissive** side for staleness, because rejecting fresh data due to
our own clock error is a self-inflicted outage, while the 250 ms of extra tolerance is far inside the
safety margins of a 2 s gate.

## 3. Which timestamp drives which decision

Ambiguity here is what makes replay and skew dangerous, so each is pinned explicitly.

| Decision | Basis | Compared against |
|---|---|---|
| Telemetry freshness gate (2 s) | `eventTimeUtc` | wall-clock **now** |
| Prediction TTL gate (10 s) | `predictedAtUtc` | wall-clock **now** — **never** the consumed record's position (DEC-002) |
| Feature window (60 s) | `eventTimeUtc` | window boundaries |
| 1-second aggregation bucket | `eventTimeUtc` | bucket boundaries |
| Command expiry | `expiresAtUtc` | wall-clock **now** |
| Command supersession | `sourcePredictionAtUtc` | `lastAcceptedSourcePredictionAtUtc` (DEC-003) |
| Consumer lag | Kafka offsets | not time-based |

**Aggregation uses event time; gates use wall clock.** Aggregation must be reproducible across a
restart, which requires event time. Gates must reflect reality *now*, which requires wall clock.
Using event time for a gate is precisely the bug that would make replayed predictions look fresh.

## 4. Timestamp anomalies (GAP-063)

| Anomaly | Detection | Action |
|---|---|---|
| `eventTimeUtc > ingestTimeUtc + skew_budget` | gateway | raise `TIMESTAMP_REVERSED`, `overall = UNCERTAIN`, keep the value |
| `eventTimeUtc` older than 60 s at ingest | gateway | raise `STALE_READING`, `overall = UNCERTAIN` |
| `eventTimeUtc` unparseable/absent | gateway | synthesise, raise `TIMESTAMP_SYNTHESISED` |
| `predictedAtUtc` in the future beyond budget | supervisor | reject `PREDICTION_STALE`; never accept a future prediction |
| Equipment epoch reset (`sourceEpochMs` decrease) **paired with** a `sequence` reset | gateway | restart sequence tracking; **not** `SEQUENCE_GAP` — this is a restart or the 2^32 ms wrap (`EDGE_GATEWAY.md` §14) |
| `sourceEpochMs` decrease **without** a `sequence` reset, or the reverse | gateway | raise `SEQUENCE_GAP`; the two signals disagree, so it is not a restart |

*Corrected 2026-09-17 by OD-004.* This table previously said an equipment clock reset raises
`SEQUENCE_GAP` outright, which contradicted `EDGE_GATEWAY.md` §14 and would have turned every
49.7-day epoch wrap into a reported data-loss incident. The paired-signal rule above is now the
single statement of this behaviour, and §14 is its normative home.

A reversed timestamp is kept rather than dropped because the *value* may still be good; it is the
*timing* that is untrustworthy, and `UNCERTAIN` correctly bars it from safety-required use while
leaving it available for trend display.

## 5. Quality flag vocabulary — closed enum (GAP-061)

v0.2 allowed arbitrary strings, so the `SENSOR_QUALITY_BAD` gate had no decidable input domain and
metric cardinality was unbounded. The enum is now **closed and carried in the JSON Schema itself**,
not merely in prose.

| Flag | Raised by | Meaning | Value | Contributes |
|---|---|---|---|---|
| `SENSOR_MISSING` | gateway | no reading available | `null` | BAD |
| `SENSOR_DISCONNECTED` | gateway | protocol reports no communication | `null` | BAD |
| `SENSOR_FROZEN` | gateway | value unchanged > `frozen_threshold` (30 samples) | as read | BAD |
| `VALUE_OUT_OF_RANGE` | gateway | outside the §1.2 valid range | `null` | BAD |
| `VALUE_NOT_FINITE` | gateway | source produced NaN/Inf | `null` | BAD |
| `STALE_READING` | gateway | source timestamp older than 60 s | as read | UNCERTAIN |
| `OUTLIER_SUSPECTED` | gateway | > 6σ from trailing 60 s mean | as read | UNCERTAIN |
| `TIMESTAMP_REVERSED` | gateway | event after ingest | as read | UNCERTAIN |
| `TIMESTAMP_SYNTHESISED` | gateway | no source timestamp (all Modbus) | as read | *(none)* |
| `DUPLICATE_SUSPECTED` | gateway | repeated `(equipmentId, sequence)` | as read | UNCERTAIN |
| `SEQUENCE_GAP` | gateway | sequence increment > 1 | as read | UNCERTAIN |
| `BUFFER_OVERFLOW_DROP` | gateway | events dropped before this one | as read | UNCERTAIN |

`TIMESTAMP_SYNTHESISED` contributes nothing to `overall` — it is the normal, permanent condition for
every Modbus device, and letting it degrade quality would mark all Modbus equipment permanently
`UNCERTAIN` and disable AI on it forever.

## 6. Deriving `quality.overall` (DEC-008)

**Owner: the Edge Gateway.** It owns normalisation (`SYSTEM_ARCHITECTURE.md` §2), so it is the only
component with both the protocol quality and the sensor map. The simulator reports raw protocol
quality only; it never computes `overall`.

```text
IF any SAFETY-REQUIRED channel has a BAD-contributing flag        -> overall = BAD
ELSE IF any channel has an UNCERTAIN-contributing flag            -> overall = UNCERTAIN
ELSE                                                              -> overall = GOOD
```

Safety-required vs advisory is defined in `EQUIPMENT_MODEL_AND_STATE.md` §4
(required: `vibrationRms`, `temperatureC`, `currentA`, `rpm`, `operationRatePct`;
advisory: `torqueNm`, `voltageV`).

`overall` is **derived, never free-form**. A contract test asserts that for every telemetry fixture,
`overall` equals the value this rule produces from the flags.

## 7. Representing a dead sensor (GAP-062)

v0.2 made this impossible: all seven measurements `required`, `additionalProperties:false`, and JSON
has no NaN. A producer had to fabricate a value or emit an invalid event.

| Rule | |
|---|---|
| All seven measurement **keys** remain `required` | shape stays stable for consumers |
| **Values** become `["number","null"]` | `null` = no trustworthy value exists |
| Every `null` **must** carry a corresponding quality flag | a bare null is a contract violation |
| **Substituting a synthetic, zero, or last-known value is forbidden** | it would feed the gates a fabricated reading |

Interpolation or imputation may occur **only** in explicitly-labelled derived feature aggregates
(§8), never in the raw telemetry contract.

## 8. Null handling in feature aggregation

Codex raised this in Round 1 and it is the subtlest item here: a 60 s feature window containing nulls
must aggregate **deterministically across a feature-builder restart**, or training-serving skew
appears out of nothing.

| Rule | |
|---|---|
| Nulls are **excluded** from aggregate statistics | never zero-filled — a zero is a physically meaningful reading and would corrupt mean/std |
| Every aggregate carries `validSampleRatio` | `valid samples / expected samples` for that channel |
| Expected sample count is **derived from window duration and cadence**, not from records received | otherwise a restart mid-window changes the denominator and the same data yields different features |
| `validSampleRatio < 0.8` on any safety-required channel | window yields `FEATURE_WINDOW_INSUFFICIENT`, **no prediction is produced at all** |
| `validSampleRatio < 0.8` on an advisory channel only | prediction proceeds; ratio is carried on the prediction event |
| Window boundaries are **absolute event-time boundaries** | aligned to wall-clock second boundaries, not to arrival, so they are restart-invariant |

**No prediction is better than a low-confidence prediction** here. A prediction derived from a
half-empty window would be assigned a confidence by a model that was never trained on half-empty
windows — the confidence itself would be out of distribution. Suppressing the prediction produces a
clean `AI_UNAVAILABLE` fallback instead of a confidently wrong recommendation.

## 9. Frozen-sensor detection

A frozen sensor is the most dangerous failure here, because it reports a plausible value forever and
a naive range check passes it.

| Parameter | Value |
|---|---|
| Detection | bit-identical value for `frozen_threshold` = **30 consecutive samples** (3 s at 100 ms) |
| Exempt | `operationRatePct` when the commanded setpoint is genuinely constant |
| Action | `SENSOR_FROZEN` → contributes BAD → `overall = BAD` if safety-required |

`operationRatePct` must be exempt: a steady machine legitimately reports a constant rate, and without
the exemption every stable machine would be flagged frozen within 3 s.
