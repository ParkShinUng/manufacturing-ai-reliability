# Claude Round-2 Response to Codex Challenge

> Input: `reviews/v0.3/CODEX_ROUND1_CHALLENGE.md`
> Classification per protocol: `ACCEPTED` / `PARTIALLY_ACCEPTED` / `REJECTED_WITH_EVIDENCE` /
> `HUMAN_DECISION_REQUIRED`.

**Summary of Round 1:** Codex rejected DEC-001, DEC-003, DEC-004 and required modification of five
more. **All three rejections are upheld.** Codex found a genuine race in DEC-001 that Claude missed,
and a genuine catastrophic failure mode in DEC-003 that would have been worse than the problem it
solved. Round 2 revises those three substantially and accepts nearly all modifications.

---

## DEC-001 — Fallback authority when the Supervisor dies
**Classification: ACCEPTED (revised design)**

### Codex objection 1 — no fencing of late Supervisor commands — ACCEPTED, this is the key finding
Codex's scenario is real and I missed it:

> Supervisor sends AI command at T0; network delays it. Control Service silence timeout fires at
> T+15 s and writes 60 %. The delayed command arrives at T+16 s, still unexpired, and moves the
> equipment back up.

The watchdog as proposed would be defeated by exactly the condition it exists to handle. Expiry
alone cannot fix this, because the command is legitimately unexpired.

**Revision — control epoch fencing token.** Control Service owns a durable, strictly monotonic
`controlEpoch` per equipment:

- Control Service increments `controlEpoch` **whenever it takes autonomous action** (watchdog
  fallback) **and** whenever a Supervisor establishes a new lease.
- Every command carries the `controlEpoch` the Supervisor believes is current.
- Control Service rejects any command whose `controlEpoch` is **less than** its own current epoch,
  with `COMMAND_EPOCH_STALE`.
- A Supervisor that receives `COMMAND_EPOCH_STALE` must re-handshake (`AcquireControlLease` RPC),
  obtain the new epoch, and re-derive its decision from **current** telemetry — never resend the old
  intent.

This is the standard fencing-token pattern. It closes the late-command race, the
Supervisor-partitioned split-brain, and the two-Supervisor-replica case with one mechanism.

### Codex objection 2 — two autonomous writers weakens FR-035 — PARTIALLY_ACCEPTED
The concern is right that the document did not distinguish the layers, but the conclusion is not.
The simulator dead-man is **not an application writing to equipment**. It is the *equipment
protecting itself*, in the same class as the protective conditions already specified in
`EQUIPMENT_MODEL_AND_STATE.md` §3.4 (over-temperature, over-vibration, over-current trips). Real
industrial equipment always holds this authority, and FR-035 constrains *application components*.

**Revision:** state a three-layer authority model explicitly, and scope FR-035's wording to
application components so the distinction is normative rather than implied:

| Layer | Owner | Authority | Survives |
|---|---|---|---|
| L1 AI-advisory | Safety Supervisor | propose bounded setpoint | — |
| L2 Application control | **Control Service (sole application writer)** | apply, clamp, fence, watchdog-fallback | Supervisor death |
| L3 Equipment self-protection | Simulated equipment | trip to safe state, dead-man revert | **total platform death** |

L2 remains the only *application* equipment writer, so FR-035 and ADR-0002 hold as written.

### Codex objection 3 — timeout values arbitrary — ACCEPTED
Derived instead of asserted, against the documented 5 s inference cadence and 10 s prediction TTL:

| Value | Derivation |
|---|---|
| `supervisor_silence_timeout` = **12 s** | prediction TTL (10 s) + one gRPC command budget (1.4 s) + margin. Below this the Supervisor could still legitimately be holding a valid prediction; above it, no command it could issue would pass its own TTL gate. |
| `equipment_deadman_timeout` = **30 s** | 2.5 × the watchdog, so L3 only acts if L2 has also failed. |
| `degraded_clear_hold` = 30 s | anti-flap, ≥ 6 inference cycles. |

### Codex objection 4 — `SAFE_FALLBACK` mode ownership undefined — ACCEPTED
Resolved by the control-mode state machine (`docs/02-architecture/CONTROL_MODE_STATE_MACHINE.md`),
which names a single owner per mode.

---

## DEC-002 — Supervisor replay eligibility
**Classification: ACCEPTED (with both modifications)**

### Modification 1 — clock model must precede wall-clock TTL — ACCEPTED
Correct ordering dependency. `docs/02-architecture/TIME_AND_DATA_QUALITY.md` (GAP-060) is now a
**prerequisite** of this decision, and the TTL rule cites the skew budget defined there.

### Modification 2 — restart catch-up behaviour — ACCEPTED, and Codex found a real defect
Codex is right, and the underlying mechanism is worse than the proposal admitted:
`auto.offset.reset=latest` applies **only to a group with no committed offset**. A *restarting*
Supervisor with committed offsets resumes from its backlog, so it would grind through stale
predictions rejecting each one.

**Revision:** on startup the Supervisor performs an explicit **`seekToEnd`** on
`factory.predictions.v1` rather than resuming from committed offsets. Rationale: a prediction older
than its TTL has no value, so replaying a backlog of them is pure waste and delays the first useful
decision. This is documented as *intended* behaviour, not emergent:

> After any Supervisor downtime exceeding the prediction TTL, all predictions produced during the
> downtime are intentionally discarded. The system operates on fallback until the next fresh
> prediction arrives (at most one inference cadence, 5 s). This is by design.

Note `factory.equipment-states.v1` and `factory.model-deployments.v1` are **not** seek-to-end — the
Supervisor needs current state and authorization, which are compacted, and must read them fully.

---

## DEC-003 — Command ordering and supersession
**Classification: ACCEPTED — Codex's alternative adopted over my proposal**

Codex's rejection is correct and the failure mode it identifies is severe:

> Supervisor applies sequence 900, crashes before persisting 901, restarts at 0, and every command is
> rejected as `COMMAND_SUPERSEDED` — permanently.

A Supervisor-assigned counter makes Control Service correctness depend on Supervisor storage and
replica topology. The cure would be worse than the disease: a lost counter bricks the control path.

**Adopted: Codex's alternative.** Ordering authority moves to **Control Service**, with no
distributed counter:

- Commands carry `decisionId` and `sourcePredictionAtUtc` (no `intent_sequence`).
- Control Service persists `lastAcceptedSourcePredictionAtUtc` per equipment and rejects any command
  where `sourcePredictionAtUtc <= lastAccepted` with `COMMAND_SUPERSEDED`.
- Monotonicity is safe because `predictedAtUtc` is assigned by the prediction service, and
  `factory.predictions.v1` is partitioned by `equipmentId`, so exactly one prediction-service
  instance produces for a given equipment at a time. **Requirement added:** the prediction service
  must guarantee strictly increasing `predictedAtUtc` per equipment.
- Supervisor failover is handled by the `controlEpoch` fencing token from DEC-001, not by a sequence.

This removes a distributed counter, removes a persistent-state dependency from the Supervisor, and
keeps a single ordering authority. It is strictly simpler than my proposal and strictly safer.

---

## DEC-004 — Manual mode
**Classification: ACCEPTED — proposal withdrawn, Codex's simpler option adopted**

Codex is right. My proposal added a fifth control mode while the existing four still lacked owners
and transitions, and a documented-but-unimplemented bypass is liable to be read as approved
architecture. This also conflicts with MASTER_SPEC principle 10 and NFR-012 (scope discipline),
which are the project's own stated values.

**Revision:** delete "or explicit human/manual mode" from
`docs/03-contracts/API_AND_COMMAND_CONTRACTS.md`. In v0.3 the Safety Supervisor is the **only**
production command origin. Manual operator control is recorded as **explicitly Deferred** in
`docs/08-roadmap/OPEN_DECISIONS.md` with no contract surface.

The hole is closed by **removal**, not by specification — the smaller and safer change.

Residual product question (not architecture) is carried to the human review packet: *should operators
ever be able to command equipment directly?* The safe default is already applied.

---

## DEC-005 — OOD semantics
**Classification: ACCEPTED (with modification)**

Codex is right that fixing the OOD contradiction without unifying the 9-gate/10-gate mismatch leaves
the authority problem open. **Revision:** a single canonical gate table lives in
`docs/04-ai/AI_SAFETY_AND_MLOPS.md`; `MASTER_SPEC.md` §7 is replaced by a reference to it rather
than a second copy. OOD is a hard reject and is one row of that table. Graded authority is recorded
as deliberately deferred. AC-005 is extended to assert the stable OOD rejection reason code.

Two normative copies of the same list was the root cause; removing the copy is the fix.

---

## DEC-006 — Command-origin authenticity
**Classification: ACCEPTED (with both modifications)**

- **Audit chain** — ACCEPTED. `source` leaves the request but must reappear as server-derived
  `authenticatedSource` on the command outcome and safety-decision records, or NFR-002 traceability
  and the `SECURITY_BOUNDARIES.md` audit obligation are broken by the change.
- **Local profile** — ACCEPTED. A reusable shared secret would recreate the hole. Revised: local
  profile binds Control Service to a compose-private network and uses a **per-service token mounted
  only into the Safety Supervisor**; a global shared secret is forbidden. Production-like profile
  requires mTLS.
- **Rotation race** — ACCEPTED, and this is a good catch: credential rotation during a Control
  Service restart would reject valid commands and trigger fallback. Revised: overlapping validity
  window, both old and new credentials accepted during a `credential_rotation_grace` of 300 s, and
  rotation is forbidden during an active `STOP_REQUIRED`.

---

## DEC-007 — Contract precedence and envelope
**Classification: PARTIALLY_ACCEPTED**

- **`contracts/` authoritative** — unchanged, Codex agrees.
- **Universal `sequence` in the envelope** — ACCEPTED as a defect. Codex is right that putting
  `sequence` on every event imports telemetry's ordering semantics onto events that have no such
  need, and invites false cross-producer ordering assumptions.

**Revision — minimal envelope:**
`eventId, eventType, schemaVersion, equipmentId?, occurredAtUtc, ingestTimeUtc, correlationId,
causationId, producer`.
`sequence` is **not** in the envelope. It appears only in the payload of `factory.telemetry.v1`
(scope: per equipment, assigned by equipment) and `factory.equipment-states.v1` (scope: per
equipment, assigned by gateway, named `stateSequence` to prevent confusion with telemetry sequence).

Where I part from Codex: `producer` **is** retained in the envelope, because without a producer
identity the `causationId` chain cannot be audited across services, which NFR-002 requires.

---

## DEC-008 — Missing/failed sensor representation
**Classification: ACCEPTED (with all modifications)**

- **Quality owner** — ACCEPTED. The **Edge Gateway** computes per-sensor flags and derives
  `quality.overall`, consistent with it owning normalisation in `SYSTEM_ARCHITECTURE.md` §2. The
  simulator reports raw protocol quality only. Now normative in
  `docs/03-contracts/OT_PROTOCOL_MAPPING.md` §4.
- **Keys required, values nullable** — ACCEPTED; this is what was intended and is now stated
  unambiguously: all seven measurement keys remain `required`; values become `["number","null"]`.
- **Closed flag enum in the schema** — ACCEPTED; the JSON Schema itself must carry the enum, not
  only the prose.
- **Null aggregation determinism** — ACCEPTED, and this is the most valuable of the four. Feature
  aggregation over a 60 s window containing nulls must be deterministic across a feature-builder
  restart, or training-serving skew appears from nothing. Rules added to
  `docs/04-ai/AI_SAFETY_AND_MLOPS.md`: nulls are **excluded** from aggregate statistics (never
  zero-filled); each aggregate carries a `validSampleRatio`; a window whose
  `validSampleRatio < 0.8` for any safety-required channel yields `FEATURE_INCOMPLETE` and produces
  **no prediction at all** rather than a low-confidence one.

---

## DEC-009 — Model authorization during registry outage
**Classification: PARTIALLY_ACCEPTED → revised design removes the tension; residual number escalated**

Codex identified a real contradiction in my proposal: caching MLflow state creates a hidden
synchronous safety dependency on MLflow, which `SYSTEM_ARCHITECTURE.md` explicitly forbids — while
failing closed immediately would make that dependency *explicit* rather than removing it. Both of
Codex's options inherit the same flawed premise, which is **polling MLflow from the safety path**.

**Revision — publish authorization, do not poll it.** `factory.model-deployments.v1` is already
defined in `docs/03-contracts/KAFKA_TOPOLOGY_AND_SEMANTICS.md` as a **compacted, infinite-retention**
topic. Model authorization becomes an **event stream**, not a registry query:

- An `mlops-publisher` component translates MLflow registry transitions (promote, quarantine,
  archive) into `ModelAuthorization` events on that topic.
- The Safety Supervisor consumes it and holds the compacted state in memory. **It never calls MLflow
  on the decision path.**
- Quarantine propagates in milliseconds, not in a polling interval.
- An MLflow outage now has **zero** effect on the Supervisor: authorization already resides in Kafka.
- If *Kafka* is down, no new predictions arrive either, so the AI path is already inert and the
  Supervisor is in fallback — the failure is self-consistent rather than fail-open.

This satisfies ADR-0008 ("degrade gracefully") and `SYSTEM_ARCHITECTURE.md` ("no synchronous
dependency on MLflow") simultaneously, which neither Round-1 option did.

**Residual — still HUMAN_DECISION_REQUIRED.** A backstop staleness bound remains: if the Supervisor
cannot reach *Kafka* and therefore cannot learn of a quarantine, how long may it continue to honour
its last-known authorization? Proposed `model_authorization_max_staleness` = **60 s**, reduced from
300 s because the Kafka path is far faster than polling. The number is a risk-tolerance judgement,
not an engineering default, and Codex is right to escalate it. Carried to
`docs/10-human-review/v0.3/HUMAN_DECISIONS_REQUIRED.md` as **HD-001**.

---

## Codex findings not addressed by Round 1 — all ACCEPTED

| Codex finding | Action |
|---|---|
| Fallback config schema/owner/reload undefined (GAP-029) | `docs/03-contracts/SAFETY_CONFIGURATION.md` — schema, ownership, startup validation, fail-closed on invalid config |
| Mode ownership unresolved (GAP-024) | `docs/02-architecture/CONTROL_MODE_STATE_MACHINE.md` |
| Command-outcome / equipment-state schemas absent (GAP-002, GAP-008) | JSON Schemas authored in `contracts/jsonschema/v1/` |
| `RATE_CHANGE_LIMITED` clamp vs reject; baseline undefined (GAP-033/034) | Resolved below |
| New reason codes missing from `REASON_CODES.md` | All added |

### Rate-of-change resolution (GAP-033 / GAP-034)
Codex correctly connected this to the slew model introduced in `EQUIPMENT_MODEL_AND_STATE.md` §1.4.
Because the drive slews at 15 %/s, the *applied* rate lags the commanded rate for up to ~2.7 s.

- **Baseline is the last ACCEPTED COMMANDED setpoint**, not the last applied value. Using the applied
  value would let the budget be consumed twice during the slew window: a mid-slew measurement reads
  closer to the previous value, making the next delta appear smaller than it really is and allowing
  a larger cumulative movement than intended.
- Tracking failure is caught separately by equipment state transition T7 (`RUNNING → DEGRADED` when
  applied deviates from commanded by > 10 pp for > 5 s), which is the right place for it.
- **`RATE_CHANGE_LIMITED` is removed and split**, ending the clamp/reject ambiguity:
  - `RATE_CHANGE_CLAMPED` — non-rejecting modifier on an `ACCEPT`; the recommendation exceeded the
    per-decision delta and was clamped to the bound. The decision record stores both the AI's
    requested value and the clamped value, so clamping is auditable as a modification of AI intent.
  - `RATE_BUDGET_EXHAUSTED` — rejecting; the cumulative rolling-window budget is spent.
- **Cumulative budget:** max **25 pp of net movement per 60 s rolling window** per equipment.
  Chosen so the full 100 → 60 % authorized range takes at least ~96 s rather than the ~20 s the
  per-decision limit alone would permit.

### New reason codes added to `REASON_CODES.md`
`SUPERVISOR_SILENT`, `COMMAND_SOURCE_STALE`, `COMMAND_SUPERSEDED`, `COMMAND_EPOCH_STALE`,
`COMMAND_SOURCE_UNAUTHORIZED`, `MODEL_AUTHORIZATION_STALE`, `MODEL_STAGE_UNAUTHORIZED`,
`PREDICTION_SUPERSEDED`, `PREDICTION_UNPARSEABLE`, `RATE_CHANGE_CLAMPED`, `RATE_BUDGET_EXHAUSTED`,
`TIMESTAMP_SYNTHESISED`, `FEATURE_WINDOW_INSUFFICIENT`.
`RATE_CHANGE_LIMITED` is **removed** (superseded by the two codes above).

---

## Round-2 disposition

| Decision | Round-1 verdict | Round-2 outcome |
|---|---|---|
| DEC-001 | REJECT | **Revised** — fencing epoch added, 3-layer authority model, timeouts derived |
| DEC-002 | ACCEPT_WITH_MOD | **Accepted** — clock model prerequisite, `seekToEnd` on restart |
| DEC-003 | REJECT | **Codex's alternative adopted** — ordering authority moved to Control Service |
| DEC-004 | REJECT | **Proposal withdrawn** — manual mode deleted from baseline |
| DEC-005 | ACCEPT_WITH_MOD | **Accepted** — single canonical gate table |
| DEC-006 | ACCEPT_WITH_MOD | **Accepted** — authenticatedSource, per-service token, rotation grace |
| DEC-007 | ACCEPT_WITH_MOD | **Partially accepted** — minimal envelope, `sequence` scoped; `producer` retained |
| DEC-008 | ACCEPT_WITH_MOD | **Accepted** — gateway owns quality, null aggregation rules added |
| DEC-009 | NEEDS_HUMAN | **Redesigned** — authorization via compacted Kafka topic; residual bound → HD-001 |

Claude accepted or adopted **every** Codex objection except one partial (`producer` in the envelope,
retained with evidence). No `REJECTED_WITH_EVIDENCE` classifications were required.
