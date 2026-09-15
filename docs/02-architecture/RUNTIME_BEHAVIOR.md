# Runtime Behavior

## Baseline equipment
A variable-speed motor-driven conveyor/drive unit is modeled with:
- motor temperature;
- vibration RMS;
- current;
- voltage;
- RPM;
- torque;
- derived operation rate.

Fault profiles:
- gradual bearing degradation;
- overload;
- cooling degradation/overheat;
- sensor drift/noise;
- communication interruption;
- OOD operating profile.

## Scale profiles
- Demo: 20 equipment, each emits one canonical telemetry event every 100 ms.
- Load: configurable up to 250 equipment. The number is a test configuration, not a guaranteed performance claim.

## Feature timing
- raw event cadence: 100 ms;
- one-second aggregates: mean/std/min/max/slope or domain-specific equivalents;
- model feature window: trailing 60 seconds;
- inference cadence: every 5 seconds per equipment;
- prediction TTL: 10 seconds;
- telemetry freshness gate: 2 seconds.

## Simulator operation-rate policy
- nominal: 100%;
- deterministic fallback: 60%;
- AI-authorized range: 60–100%;
- maximum accepted rate movement: **10 percentage points per accepted recommendation**;
- **cumulative budget: 25 percentage points of net movement per rolling 60-second window per
  equipment** (v0.3, ADR-0013). Without this, the per-decision limit alone permits 100 % to 60 % in
  roughly 20 s at a 5 s cadence; the window budget stretches the full authorized range to at least
  ~96 s;
- **the delta baseline is the last ACCEPTED COMMANDED setpoint**, never the last applied
  `operationRatePct`. The drive slews at 15 %/s
  (`EQUIPMENT_MODEL_AND_STATE.md` §1.4), so a mid-slew reading sits between the old and new values
  and would make the next delta look smaller than it is, letting the budget be spent twice. Tracking
  failure is caught separately by equipment transition T7.

These values exist only for the simulated equipment profile and must not be presented as real-world
safety limits. Equipment physics, sensor ranges, degradation equations, and the equipment state
machine are in [`EQUIPMENT_MODEL_AND_STATE.md`](EQUIPMENT_MODEL_AND_STATE.md); the enforcing
configuration contract is [`../03-contracts/SAFETY_CONFIGURATION.md`](../03-contracts/SAFETY_CONFIGURATION.md).

## Persistence
- raw 100 ms telemetry: Kafka short-term retention only in baseline;
- 1-second telemetry aggregates: PostgreSQL;
- prediction/safety decision/command/fault/model deployment records: PostgreSQL;
- dashboard queries PostgreSQL through Operations Service and reads platform metrics from approved endpoints.
