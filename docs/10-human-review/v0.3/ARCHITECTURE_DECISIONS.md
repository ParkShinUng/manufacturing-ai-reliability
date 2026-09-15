# Architecture Decisions — v0.3 Summary for Human Review

> Significant decisions only. Full challenge transcripts: [`docs/09-decisions/`](../../09-decisions/README.md).
> Final architecture statements: [`docs/07-adr/`](../../07-adr/).

---

## DEC-001 / ADR-0011 — Fallback authority when the Safety Supervisor dies

**Problem.** The Supervisor was the only command origin, and `SAFE_FALLBACK` was a mode *of the
Supervisor*. Kill it and nothing commanded anything: equipment held a possibly AI-elevated rate
indefinitely. MASTER_SPEC principle 3 was therefore only true for inference failure.

**Selected.** Three-layer authority: L1 Supervisor advises, L2 Control Service applies and runs a
12 s fallback watchdog, L3 equipment holds protective trips and a 30 s dead-man. A durable
`controlEpoch` fences commands issued under a superseded lease.

**Claude position.** Watchdog + dead-man (Round 1).
**Codex position.** **REJECT** — two autonomous writers weakens FR-035, and crucially *no fencing of
late Supervisor commands*: a command sent at T0 and delayed arrives at T+16 s after the watchdog
fired at T+12 s, still unexpired, and undoes the fallback.
**Convergence.** Claude accepted; added epoch fencing, stated the three layers explicitly, derived
the timeouts from the documented TTL and gRPC budget rather than asserting them. Codex then required
the epoch be actually contracted and durably persisted; done in Round 3.

**Trade-off.** Durable per-equipment state in Control Service and a lease handshake, versus an
unfenced control path.
**Human review: YES (HD-003)** — it changes who may move equipment.

---

## DEC-003 / ADR-0013 — Command ordering without counters or clocks

**Problem.** Ordering was decided by arrival, so an older intent could win.

**Selected.** Make concurrency **structurally impossible**: command validity 2 s < inference cadence
5 s, at most one outstanding command per equipment, per-equipment serialisation, plus epoch fencing.

**Claude position.** Supervisor-assigned monotonic `intent_sequence` (Round 1), then ordering by
`sourcePredictionAtUtc` (Round 2).
**Codex position.** **REJECT** both. On the counter: *"Supervisor applies 900, crashes before
persisting 901, restarts at 0, and every command is rejected as superseded — permanently."* On the
timestamp: *"`predictedAtUtc` is a wall clock; no monotonic allocator survives restart, rebalance, or
a backward clock step. Kafka partitioning orders records after production; it does not make producer
timestamps monotonic."*
**Convergence.** Both Claude proposals were wrong and both were withdrawn. The adopted design has no
distributed failure mode at all.

**Trade-off.** A command must land within 2 s or be re-derived. The gRPC profile fits comfortably.
**Human review: no.**

---

## DEC-004 / ADR-0014 — Manual operator command origin removed

**Problem.** The contract permitted *"explicit human/manual mode"* to originate production commands,
with nothing defining that mode — a documented bypass of the safety path. **Found by Codex only.**

**Selected.** Removed from the baseline; recorded as Deferred with no contract surface.

**Claude position.** Specify it properly as a fifth control mode; implement at P1.
**Codex position.** **REJECT** — spec bloat while the four existing modes were still undefined, and
*"a documented-but-unimplemented bypass may be mistaken as approved architecture."*
**Convergence.** Claude withdrew. The hole is closed by **removal**, which is both smaller and safer
than specification.

**Trade-off.** No direct operator control in the baseline.
**Human review: YES (HD-002)** — product scope, not architecture cleanup.

---

## DEC-009 / ADR-0019 — Model authorization published, not polled

**Problem.** A model quarantined *during* an MLflow outage would keep control authority — fail-open
in the one direction that matters.

**Selected.** `mlops-publisher` publishes authorization to a compacted Kafka topic; the Supervisor
never calls MLflow on the decision path. A 30 s `AuthorizationWatermark` makes publisher death
detectable within 90 s, after which all models become unauthorized.

**Claude position.** Cache with a 300 s staleness bound (Round 1).
**Codex position.** **NEEDS_HUMAN_DECISION** — five minutes is a risk decision, not an engineering
default; and caching creates *"a hidden synchronous safety dependency on MLflow, contrary to
`SYSTEM_ARCHITECTURE.md`."* Then in Round 2, against the redesign: *"if quarantine occurs while
`mlops-publisher` is down, Kafka remains reachable and quiet, so the Supervisor never trips the
staleness bound."*
**Convergence.** Claude noted both of Codex's Round-1 options shared the flawed premise of polling
MLflow at all, and redesigned. Codex's Round-2 finding — that **silence is indistinguishable from
"nothing changed"** — was the sharpest of the review and produced the watermark.

**Trade-off.** One new component, justified under NFR-012 because it removes a synchronous safety
dependency and its own failure fails closed.
**Human review: YES (HD-001)** — the residual staleness bound.

---

## DEC-005 / ADR-0015 — OOD is a hard reject; one canonical gate table

**Problem.** `MASTER_SPEC` said any failed gate rejects; `FAILURE_MODEL` said OOD may *"reject or
reduce AI authority."* Two safety semantics on one path. Separately, two normative gate lists existed
(9 vs 10, different orders) and `REASON_CODES` implemented the union of both.

**Selected.** Hard reject. One canonical table in `AI_SAFETY_AND_MLOPS.md`; `MASTER_SPEC` references
it rather than carrying a copy.

**Codex position.** Accepted the hard reject but noted Claude had fixed only the OOD contradiction
and not the gate-list authority problem, and proposed the reference approach.
**Convergence.** Root cause was **two normative copies existing at all** — synchronising them would
have left the defect latent. The duplicate was removed.

**Human review: no.**

---

## DEC-006 / ADR-0016 — Command origin authenticated, never self-asserted

**Problem.** `source` was a string the caller set itself, yet the contract relied on it to restrict
the command path. Any process reaching the gRPC port could claim to be the Supervisor — defeating
ADR-0002 entirely.

**Selected.** mTLS workload identity; `source` removed from the request; origin derived server-side
and recorded as `authenticatedSource` on the audit record.

**Codex position.** Accepted with three modifications, all adopted: the audit chain needs a
server-derived origin or NFR-002 breaks; a reusable local shared secret would recreate the hole; and
credential rotation during a Control Service restart would trigger a spurious fallback without an
overlap window.

**Human review: no.**

---

## DEC-002 / ADR-0012, DEC-007 / ADR-0017, DEC-008 / ADR-0018 — summary

| Decision | Outcome | Codex's contribution |
|---|---|---|
| **DEC-002** Replay eligibility | Supervisor not replay-eligible; wall-clock TTL; explicit `seekToEnd` at startup | Found that `auto.offset.reset=latest` does **not** govern a restarting consumer with committed offsets — the proposal was weaker than claimed |
| **DEC-007** Contract precedence | Machine-readable schemas outrank prose; minimal envelope | Rejected a universal `sequence` field as importing unresolved ordering semantics into every event. Claude retained `producer` against Codex's minimalism, with evidence — the only surviving divergence |
| **DEC-008** Sensor representation | Nullable values, closed quality enum, gateway-derived `overall` | Required naming the owner of `overall`, and required contracting **deterministic null aggregation** — without which a feature-builder restart manufactures training-serving skew from nothing |

---

## Decisions by required human attention

| Human review required | Resolved between agents |
|---|---|
| DEC-001 (HD-003), DEC-004 (HD-002), DEC-009 (HD-001) | DEC-002, DEC-003, DEC-005, DEC-006, DEC-007, DEC-008 |
