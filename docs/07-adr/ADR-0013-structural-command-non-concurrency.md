# ADR-0013 — Command ordering by structural non-concurrency, not by counters or clocks
Status: Accepted (v0.3, dual-agent reviewed)
Decision Record: [DEC-003-command-ordering](../09-decisions/DEC-003-command-ordering.md)

## Context
Ordering was decided by arrival, so two unexpired commands arriving out of order could apply with the older intent winning - incorrect for a setpoint.

## Decision drivers
- Setpoint semantics require last-writer-wins by *intent*, not by arrival.
- No mechanism may create a state whose loss bricks the control path.
- Wall clocks are not monotonic across restart, rebalance, or clock step.

## Considered options
1. Supervisor-assigned per-equipment `intent_sequence`.
2. Ordering by `sourcePredictionAtUtc`.
3. TTL shorter than cadence + single outstanding command + epoch fencing.

## Decision
Do not order concurrent commands; make concurrency impossible. Command validity is 2 s while the inference cadence is 5 s, so two commands for one equipment can never be concurrently valid. The Supervisor keeps at most one outstanding command per equipment, Control Service serialises per equipment key, and `controlEpoch` fences superseded leases. `sourcePredictionAtUtc` is retained for audit and freshness only.

## Rationale
Option 3, reached only after Codex rejected options 1 and 2 in successive rounds. Option 1 makes Control Service correctness depend on Supervisor storage and replica topology: a lost counter causes every subsequent command to be rejected as superseded - permanently. Option 2 depends on producer wall-clock monotonicity, which a consumer-group rebalance or a backward clock step breaks; Kafka partitioning orders records after production, it does not make producer timestamps monotonic. Option 3 is a structural guarantee with no distributed failure mode.

## Trade-offs
A command must land within 2 s or be re-derived from a fresh decision. Acceptable: the gRPC profile (2 attempts x 500 ms) fits inside the window, and a stale setpoint is worth less than a fresh decision.

## Reliability impact
Removes a distributed counter and a persistent-state dependency from the Supervisor entirely.

## Failure impact
F10 (out-of-order prediction), F24 (late command) in `FAILURE_MODEL.md`.

## Operational impact
One fewer piece of durable state to back up, monitor, and repair.

## Security impact
None.

## Alternatives rejected
Both counter-based and timestamp-based ordering were rejected on concrete failure scenarios rather than on preference.
