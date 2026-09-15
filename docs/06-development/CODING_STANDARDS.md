# Coding Standards

## Universal
- Prefer explicit behavior over magic conventions.
- No swallowed exceptions.
- Time values use UTC internally and ISO-8601 in contracts.
- Cancellation/timeouts are mandatory for external I/O.
- Correlation IDs propagate across service boundaries.
- Logs are structured; do not log secrets or raw high-frequency telemetry by default.
- Configuration is externally supplied and validated on startup.

## v0.3 additions

- **Never substitute a value for a missing reading.** A sensor with no trustworthy value is `null`
  plus a quality flag. Zero, last-known, and interpolated values are forbidden in raw telemetry
  (ADR-0018) - a fabricated reading passes the safety gates.
- **Never derive authorization from a payload field.** Identity comes from the verified transport
  peer, never from a self-declared string (ADR-0016).
- **Commit durable state before the side effect it authorises.** The `controlEpoch` increment is
  persisted before the equipment write; the reverse order can un-fence a stale command.
- **Fail closed on unvalidatable safety input.** Invalid configuration aborts startup; it never
  starts with defaults (NFR-014).
- **Monotonic clocks for durations, wall clock only for absolute freshness.** Never subtract two
  wall-clock readings to measure an interval.
- **No blocking exporters.** Trace and metric export uses a bounded queue with drop-on-full; a
  blocking exporter turns an observability outage into a control outage.
- **Enumerate metric labels.** No unbounded label values, and never `equipmentId` as a label.

## C#/.NET
- nullable reference types enabled;
- warnings-as-errors for project code;
- async all the way for network/disk I/O; no `.Result`/`.Wait()` in runtime paths;
- bounded `Channel<T>`/queues where buffering exists;
- `BackgroundService` for workers with explicit cancellation;
- resilience policies are bounded and observable;
- domain state changes use explicit methods; avoid public mutable setters for critical state;
- xUnit for tests.

## Python
- type annotations required on public APIs;
- Pydantic (or generated schema models) at service boundaries;
- shared preprocessing/feature code lives in `mair_ml_core`;
- deterministic random seeds for reproducible model tests where applicable;
- pytest for tests;
- no notebook-only production logic.

## TypeScript/Next.js
- strict TypeScript;
- dashboard consumes Operations API; it does not access PostgreSQL/Kafka directly;
- UI never holds equipment-write credentials;
- demo fault-injection UI is visibly marked and disabled outside demo profile.
