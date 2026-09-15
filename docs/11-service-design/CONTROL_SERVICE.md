# Service Design — `control-service`

> Runtime: C#/.NET 10, `BackgroundService` + ASP.NET Core gRPC host.
> Closes: GAP-090 for this service. Implements DEC-001, DEC-003, DEC-006.
> **This is the most safety-relevant service in the platform.** It is L2 in the three-layer
> authority model and the sole *application* writer to equipment.

## 1. Purpose

Own equipment write authority. Apply bounded, authenticated, fenced, idempotent setpoint commands;
own control mode; and autonomously drive equipment to fallback when the Safety Supervisor stops
speaking.

## 2. Responsibilities

- sole application-level equipment write path (FR-035, ADR-0002);
- authenticate command origin from verified peer identity (DEC-006);
- issue and fence control leases via `controlEpoch` (DEC-001);
- final bounds clamping, independent of the Supervisor's clamping;
- idempotency for transport retries;
- own and persist control mode per equipment;
- **fallback watchdog**: drive to fallback on Supervisor silence;
- publish every outcome to `factory.control-outcomes.v1`.

## 3. Non-responsibilities

- **no AI logic of any kind** — it never evaluates a model, a confidence, or an OOD score;
- no Kafka consumption on the command path (ADR-0009);
- no telemetry normalisation;
- no equipment state ownership (that is the gateway's observed state);
- no decision about *whether AI is trustworthy* — only about whether a command is *admissible*.

The separation in the last bullet is the point: the Supervisor decides if the AI is right, the
Control Service decides if a command is legal. Both must fail independently.

## 4. Boundary and dependencies

| Dependency | Classification | Failure behaviour |
|---|---|---|
| Equipment (OPC UA / Modbus write) | **Control-critical** | `COMMAND_PROTOCOL_FAILED`; previous setpoint stands |
| PostgreSQL (epoch, mode, idempotency, budget) | **Control-critical** | see §12 — fail-closed |
| Safety config file | **Control-critical** | invalid → **startup abort** |
| Kafka (outcome publication) | Non-control-critical | buffer and continue; **never** blocks a write |
| Safety Supervisor (gRPC inbound) | Non-control-critical | absence triggers watchdog fallback |

Kafka being non-critical here is what makes ADR-0009 true in practice: an outcome that cannot be
published is queued, and the equipment write still happens.

## 5. Inputs / outputs

**In:** `AcquireControlLease`, `Heartbeat`, `ApplyCommand` (gRPC, mTLS).
**Out:** equipment setpoint write; `factory.control-outcomes.v1`; PostgreSQL state; metrics/traces.

## 6. Canonical contracts

`contracts/proto/control/v1/control.proto`, `contracts/jsonschema/v1/control-outcome.schema.json`.

## 7. Data ownership

**Owns exclusively:** `controlEpoch`, control mode, `lastCommandedRatePct`, rate budget ledger,
idempotency records, command outcome history.
**Reads:** safety configuration (read-only mount).
**Owns nothing** about telemetry, predictions, or models.

## 8. State model

Per equipment, durable in PostgreSQL:

```text
controlEpoch                       uint64, strictly monotonic
controlMode                        NORMAL_RULE | AI_ASSISTED | SAFE_FALLBACK | STOP_REQUIRED
lastCommandedRatePct               double
lastCommandedAtUtc                 timestamp
lastAcceptedSourcePredictionAtUtc  timestamp   -- supersession (DEC-003)
rateBudgetWindow                   rolling 60s ledger of applied deltas
leaseHolderIdentity                string (verified peer identity)
leaseExpiresAtUtc                  timestamp
lastSupervisorSignalAtUtc          timestamp   -- watchdog input
```

**`controlEpoch` durability rule (closes the Codex round-2 objection):** the epoch increment MUST be
committed to PostgreSQL **before** the equipment write that it authorises. If the process dies
between commit and write, the epoch is merely ahead — harmless. If the write happened first and the
commit were lost, a restarted service could re-issue an epoch a Supervisor already observed, which
would un-fence a stale command. Ordering the commit first makes the failure mode safe by
construction.

On restart, `controlEpoch` is **incremented once more** before accepting any command, so any lease
issued before the restart is fenced regardless of what was in flight.

## 9. Lifecycle

1. load and validate safety config — **abort on invalid** (never start with defaults);
2. connect PostgreSQL; load per-equipment state; if unreadable, **fail closed** (§12);
3. increment every `controlEpoch` (invalidates pre-restart leases);
4. downgrade any `AI_ASSISTED` to `SAFE_FALLBACK`; preserve `STOP_REQUIRED`;
5. connect OT write sessions; start watchdog; start gRPC; become ready.

Readiness is **not** signalled until state is loaded and the watchdog is running. A Control Service
that accepts commands before its watchdog runs is worse than one that is down.

## 10. Normal command flow

```text
1  verify mTLS peer identity          -> COMMAND_SOURCE_UNAUTHORIZED
2  validate request fields            -> INVALID_ARGUMENT (never coerce)
3  idempotency lookup                 -> return prior result, DUPLICATE
4  controlEpoch >= persisted?         -> COMMAND_EPOCH_STALE
5  lease valid and held by caller?    -> COMMAND_EPOCH_STALE
6  expiresAtUtc in future?            -> COMMAND_EXPIRED
7  sourcePredictionAtUtc within TTL?  -> COMMAND_SOURCE_STALE
8  sourcePredictionAtUtc <= lastAcceptedSourcePredictionAtUtc? -> COMMAND_SUPERSEDED
9  mode permits this command?         -> COMMAND_MODE_FORBIDS
10 equipment state permits?           -> COMMAND_EQUIPMENT_UNAVAILABLE
11 clamp to absolute bounds           -> COMMAND_RATE_CLAMPED (non-rejecting)
12 debit rate budget                  -> COMMAND_RATE_CLAMPED / reject
13 persist intent + epoch             (before the write)
14 write setpoint via OT protocol     -> COMMAND_PROTOCOL_FAILED
15 persist outcome + idempotency
16 publish outcome to Kafka (async, never blocking)
```

Steps 4–8 are the fencing and ordering core.

**The primary ordering guarantee is structural, not temporal.** Command validity is 2 s while the
inference cadence is 5 s, so two commands for one equipment can never be concurrently valid.
Combined with epoch fencing (step 4) and a single outstanding command per equipment, arrival-order
processing is safe **without any cross-service counter or clock comparison**. This replaces the
Supervisor-assigned `intent_sequence` of the first proposal, which would have bricked the control
path if the counter were ever lost.

**Step 8 is a secondary, defence-in-depth guard — never the primary mechanism.** It compares
`sourcePredictionAtUtc` against the last accepted value for that equipment. Its failure mode under a
backward clock step or a consumer-group rebalance is **asymmetric and safe**: a clock anomaly can
only cause the guard to *reject* a command that was actually valid, never to *accept* one that was
stale. A spurious rejection costs at most one inference cadence, because the Supervisor re-derives
from current telemetry on the next cycle. This is why the guard is retained even though correctness
does not depend on it — and why Codex's objection that wall clocks are not monotonic across restart
or rebalance, while correct, does not compromise the design: that objection is fatal to a clock used
as a *primary* ordering mechanism, which this is not.

## 11. The fallback watchdog (DEC-001)

```text
every 1s, per equipment:
  silence = now - lastSupervisorSignalAtUtc
  if silence > 12s and mode == AI_ASSISTED:
      controlEpoch += 1          (commit first)
      mode = SAFE_FALLBACK
      write configured fallback rate
      publish outcome: autonomous=true, reasonCode=SUPERVISOR_SILENT
```

`12 s` is derived, not chosen: prediction TTL (10 s) + one full gRPC command budget (1.4 s) + margin.
Below it, the Supervisor could still legitimately be acting on a valid prediction; above it, nothing
it could send would pass its own TTL gate.

Incrementing the epoch is what prevents the race Codex identified: a command issued before the
watchdog fired and delayed in the network arrives carrying the old epoch and is rejected with
`COMMAND_EPOCH_STALE` (F24). The Supervisor must then re-acquire a lease and **re-derive** its
decision from current telemetry — it must never resend the old intent.

The watchdog does **not** fire in `NORMAL_RULE` (AI is not expected to be speaking) or in
`STOP_REQUIRED` (already at the safest state).

## 12. Failure behaviour

| Failure | Behaviour |
|---|---|
| PostgreSQL unavailable at startup | **fail closed** — do not become ready |
| PostgreSQL unavailable at runtime | serve reads from in-memory state; **reject all new commands** (`COMMAND_MODE_FORBIDS`); watchdog still fires using in-memory state, because driving to the known fallback rate is safe without the database |
| OT write fails | 2 attempts, then `COMMAND_PROTOCOL_FAILED`; previous setpoint stands |
| Kafka unavailable | buffer outcomes (bounded 10 000, drop oldest); **never blocks a write** |
| Config invalid on reload | retain previous valid config; alert |
| Clock skew > budget | log; expiry uses wall clock with the skew budget added on the permissive side |

Rejecting new commands but still running the watchdog during a database outage is deliberate: the
fallback rate is a static configured value, so the watchdog needs no database, while accepting new
setpoints without durable epoch/idempotency state would be unsafe.

## 13. Timeout / retry / idempotency / ordering

| Aspect | Rule |
|---|---|
| gRPC inbound deadline | 500 ms |
| OT write timeout | 250 ms, 2 attempts, 100 ms backoff ±20 % |
| Idempotency key | `equipmentId:decisionId`; scope is **transport retry of one decision only** |
| Idempotency storage | PostgreSQL, durable, **survives restart** |
| Idempotency retention | 15 min (≫ 2 s command TTL) |
| Ordering | per-equipment serialised; a single worker owns an equipment key at a time |
| OT-level idempotency | `SET_OPERATION_RATE` is a **setpoint write and naturally idempotent** — this property is what makes retry safe and is stated because the design depends on it |

## 14. Backpressure / buffering

Commands are low rate (≤ 0.2/s/equipment). Inbound concurrency is bounded per equipment; excess is
rejected with `RESOURCE_EXHAUSTED` rather than queued — a queued setpoint is a stale setpoint.

## 15. Restart recovery

See §9. Invariants: epoch always advances; `AI_ASSISTED` never survives; `STOP_REQUIRED` always
survives; no command is accepted before the watchdog is live.

## 16. Configuration

`fallbackRatePct` (60), `aiAuthorizedRange` (60–100), `maxDeltaPerDecisionPp` (10),
`rateBudgetPp/window` (25 / 60 s), `supervisorSilenceTimeoutMs` (12 000), `commandTtlMs` (2 000),
`leaseDurationMs` (30 000), `idempotencyRetentionMin` (15), `clockSkewBudgetMs` (250).
All validated at startup; missing equipment entry = invalid config = abort.

## 17. Security

mTLS required (production-like). Identity → permitted command types → permitted equipment scope.
`source` is **never** read from the payload. Local profile: compose-private network plus a
per-service token mounted only into the Supervisor; a global shared secret is forbidden. Credential
rotation uses a 300 s overlapping-validity window and is forbidden during `STOP_REQUIRED`.

## 18. Observability

`commands_total{status,reason}`, `command_latency_seconds`, `duplicate_command_total`,
`command_epoch_stale_total`, `command_superseded_total`, `watchdog_fallback_total`,
`control_mode{mode}`, `control_epoch`, `rate_budget_remaining_pp`, `ot_write_errors_total`,
`outcome_publish_backlog`.
`equipmentId` goes in traces and exemplars, **never** in metric labels.

## 19. Performance targets (TARGET — unmeasured)

| Metric | Target |
|---|---|
| P95 command apply latency (receipt → OT write ack) | ≤ 150 ms |
| P99 | ≤ 400 ms |
| Watchdog detection accuracy | fires within 12 s ± 1 s |
| Sustained command rate | ≥ 50/s across 250 equipment |

## 20. Test strategy

Unit: every branch of §10 as a table-driven test; epoch fencing; budget ledger; clamping.
Integration: against the simulator; duplicate delivery; expiry; supersession.
Failure: kill Supervisor (watchdog), delayed command after watchdog (epoch fence), PostgreSQL
outage, OT write failure, restart mid-command.
Property: no sequence of admissible commands can move the rate outside 60–100 %, exceed 10 pp in one
decision, or exceed 25 pp per 60 s.

## 21. Acceptance criteria

AC-004, AC-007, AC-011, AC-012, AC-013, AC-014 (see `ACCEPTANCE_CRITERIA.md`).
