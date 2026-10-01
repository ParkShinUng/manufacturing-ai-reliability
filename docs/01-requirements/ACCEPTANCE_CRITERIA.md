# Acceptance Criteria

> v0.3 expands 10 criteria to 40 (GAP-092). Every P0 functional requirement and every P0
> non-functional requirement now has either an AC or an explicit deferral. AC-001 is corrected: v0.2
> verified "10+" against FR-001's requirement of 20, so a system could pass acceptance while failing
> the P0 requirement (GAP-093).

Each criterion is testable, mapped to requirement IDs, and assigned to a test suite.

## Equipment, simulation, OT

- **AC-001 → FR-001/006** *(verified in **Phase 2**)* — **20** simulated machines (not "10+")
  **emit** canonical telemetry containing all mandatory metadata for 30 minutes with no unhandled
  simulator or gateway exception. Canonical telemetry is produced by the Edge Gateway, so this
  criterion cannot be evaluated until Phase 2 exists.
  *Wording corrected 2026-09-16: this said "publish", which reads as Kafka publication. AC-001 maps
  to **FR-001/FR-006** — emulating 20 instances, and every event carrying its mandatory metadata.
  Kafka publication is **FR-010**, proven in Phase 3 (`EDGE_GATEWAY.md` §5.1).*
- **AC-002 → FR-004** — Disconnect a protocol endpoint, restore it, and verify gateway reconnect and
  resumed event flow **without a gateway process restart**, within 10 s (R-01).
- **AC-018 → FR-002** — Each of the **11** fault profiles produces its documented signature; with a
  fixed seed two runs are **bit-identical** (NFR-010).
- **AC-019 → FR-002/FR-052** — `OOD_PROFILE` breaks the rpm↔rate relationship while keeping every
  value in range, so a pure range check does **not** detect it and the OOD gate must.
- **AC-020 → FR-035** — Equipment protective conditions (over-temperature, over-vibration,
  over-current) latch `FAULT` and force rate 0 **with the entire platform stopped**, proving L3 is
  independent.
- **AC-021 → FR-003/005** — OPC UA and Modbus TCP clients reading the same equipment produce
  **identical canonical engineering values**, proving scaling and word order are correct.
- **AC-022 → FR-006** — Every telemetry event validates against `telemetry.schema.json`; a dead
  sensor is emitted as `null` with a quality flag, and **no synthetic value is ever substituted**
  (D-05).
- **AC-023 → FR-006** — A dropped telemetry sample produces `SEQUENCE_GAP` and increments
  `telemetry_sequence_gaps_total`; **no dropped sample within a continuous source epoch goes
  undetected** (D-03). *Clarified 2026-09-17 by OD-004:* an equipment restart or a 2^32 ms epoch
  wrap — where `sequence` and `sourceEpochMs` reset **together** — is a new epoch, not a gap, and is
  not reported as one. Either signal resetting **alone** is a gap. Both protocols must agree, which
  is why OPC UA now carries `SourceEpochMs`.
  *Phase 2 proves the counter, not its export:* `telemetry_sequence_gaps_total` is the in-process
  `TelemetryNormaliser.SequenceGaps` until the Phase 8 exporter exists (`EDGE_GATEWAY.md` §17).

  *Amended 2026-09-21 by OD-005, on measurement.* This is a **detection** criterion, and what
  counts as achievable prevention differs by protocol:

  - **OPC UA subscription** — the server samples at 50 ms and queues 10 values per item (§1.3), so a
    client that misses a publishing cycle still receives what it missed. Zero gaps after start-up
    warm-up is required.
  - **Modbus latest-register polling** — the register image holds only the newest value, so a sample
    produced and overwritten between two polls is gone. **No silent loss** is required; no loss is
    not, because the selected design cannot deliver it.

  Measured on one equipment for 60 s with both protocols running against the same simulator: the
  subscribed path received 599 of 601 distinct samples with 0 gaps; the polled path received 439 of
  601 — **73 %** — with 161 gaps and 162 duplicate reads. Every one of those 161 was reported, which
  is the criterion actually being met.

## Event platform

- **AC-003 → FR-011** *(scope narrowed 2026-09-28 by OD-007; verified in **Phase 6**)* — During and
  after a replay of a known Kafka range, the Safety Supervisor issues **no** command and no equipment
  moves (F11). This is the whole safety claim, kept on one criterion deliberately: it was previously
  bundled with two mechanical claims that Phase 3 and Phase 4 prove, and a safety guarantee split
  across three phases is owned by none of them.
- **AC-045 → FR-011** *(new 2026-09-28, OD-007; verified in **Phase 3**)* — A **replay-eligible**
  consumer group can be rewound over a known offset range and consumes exactly those records in key
  order; offsets are committed only **after** successful processing; the Safety Supervisor's group
  is non-replay-eligible **by configuration**, and a rewind attempt against it is refused rather
  than silently honoured.
- **AC-046 → FR-011** *(new 2026-09-28, OD-007; verified in **Phase 4**)* — Replaying a known offset
  range into **projection** consumer groups rebuilds the read models **byte-identically** — equal
  canonical dumps, `OPERATIONAL_DATA.md` §9a (OD-014).
- **AC-026 → FR-010/012** — Every topic exists with the documented partition count, replication
  factor, and retention; the bootstrap job is idempotent and **asserts partition-count immutability**.
- **AC-027 → FR-011** *(scope stated 2026-09-28 by OD-009)* — A schema-invalid record goes to the
  DLQ on the **first** attempt with all required DLQ headers; redrive is operator-initiated only.
  Proven against the **shared production consume-validate-DLQ component**, not a test-only path:
  the source offset is committed **only after** the DLQ produce succeeds, downstream processing does
  **not** run for that record, and no redrive happens automatically. Every later consumer is built
  on that component, which is what makes one proof cover them.
- **AC-024 → FR-020/021/022** — Every prediction carries model name, version, run ID,
  `deploymentStage`, `featureSchemaVersion`, confidence, and `validSampleRatio`.
- **AC-025 → FR-021** — When `validSampleRatio < 0.8` on any safety-required channel, **no
  prediction is emitted at all** (rather than a low-confidence one).

## AI safety and control

- **AC-004 → FR-023/NFR-001** — Kill the inference process/pod; the Supervisor changes mode to
  fallback and the Control Service remains healthy.
- **AC-005 → FR-031/033** — Inject OOD, stale, and bad-quality telemetry and verify AI rejection
  with stable reason codes; OOD produces a **hard reject** (`OOD_HIGH`), never reduced authority.
- **AC-006 → FR-032/NFR-002** *(moved to **Phase 7** 2026-09-30 by OD-011; chain start amended by
  OD-013)* — Given a correlation ID from a **live** run, retrieve the full prediction →
  safety-decision → command chain, with the prediction's feature-window reference and that window's
  per-second readings, in a single query within 200 ms. The claim is that the **real producers link
  up**, so it is proven by the first phase in which all of them exist.
- **AC-047 → FR-032/NFR-002** *(new 2026-09-30, OD-011; verified in **Phase 4**)* — Given
  schema-valid prediction, safety-decision and control-outcome records linked by `correlationId` and
  `causationId` on the real topics, the projector stores them and `GET /trace/{correlationId}`
  returns the chain in a single query within 200 ms; a link with no record is `null` or an empty array, never invented
  (OD-012). This proves store-and-query, **not** that producers link up — that is AC-006.
- **AC-007 → FR-034** — Deliver the same command twice with an identical idempotency key; the
  equipment state changes **at most once**.
- **AC-011 → NFR-001/FR-033 [ADR-0011]** — **Kill the Safety Supervisor.** Within 12 s the Control
  Service autonomously drives the equipment to the fallback rate, publishes an outcome with
  `SUPERVISOR_SILENT`, and increments `controlEpoch`.
- **AC-012 → NFR-001 [ADR-0011]** — **Stop the entire platform** including the Control Service.
  Within 30 s the equipment dead-man reverts the setpoint to its safe default.
- **AC-013 → FR-034 [ADR-0011]** — **Late-command fencing.** Delay a command past a watchdog
  fallback; it is rejected with `COMMAND_EPOCH_STALE` and does **not** move the equipment.
- **AC-014 → FR-035 [ADR-0016]** — A client presenting no valid identity, or an identity not in the
  authorization map, is rejected with `COMMAND_SOURCE_UNAUTHORIZED`; `source` cannot be asserted in
  the payload because the field does not exist.
- **AC-015 → FR-031 [ADR-0015]** — Each of the 13 safety gates is exercised independently and in
  combination; **no gate combination yields ACCEPT unless all 13 pass**.
- **AC-016 → FR-031** — `RATE_CHANGE_CLAMPED` yields an `ACCEPT` recording both the requested and
  bounded values; `RATE_BUDGET_EXHAUSTED` yields a rejection. The two are never conflated.
- **AC-017 → FR-031** — Cumulative movement cannot exceed 25 pp in any 60 s rolling window,
  regardless of how many recommendations are accepted.
- **AC-038 → FR-033** — `STOP_REQUIRED` cannot be exited by timeout or by AI; only by operator
  acknowledgement with a recorded `operatorId` (M7).
- **AC-039 → FR-030/035** — Static analysis plus a runtime check confirm that no AI service holds
  equipment credentials and no component other than the Control Service performs an equipment write.
  For Modbus this is proven directly: a write function code sent to the gateway's read-only listener
  is refused with exception `0x01` (OD-003).
- **AC-040 → NFR-014** — Invalid safety configuration causes **startup abort**, not a start with
  defaults.

## MLOps

- **AC-008 → FR-040/041** — A deployed prediction is traceable to its MLflow model version and
  training run.
- **AC-010 → FR-042/043 [P1]** — Deploy a regressed candidate to a canary cohort, detect the
  pre-defined degradation, and demonstrate rollback.
- **AC-033 → FR-041 [ADR-0019]** — **Quarantine propagation**: quarantine a model and verify the
  Supervisor rejects its predictions within **2 s** (L-10).
- **AC-034 → FR-041 [ADR-0019]** — **Publisher death**: kill `mlops-publisher`; within 90 s the
  Supervisor treats all models as unauthorized and falls back (R-07).
- **AC-032 → FR-040/NFR-013** — Training and serving produce **byte-identical features** from the
  same input through `mair_ml_core`.

## Data, API, UI

- **AC-009 → FR-050/051** — Dashboard displays equipment, AI/control, and platform-health state from
  live APIs, not static mocks, **including the active rejection or fallback reason**.
- **AC-028 → NFR-004** *(defined 2026-09-30 by OD-014)* — A projection rebuilt from Kafka over a
  replayed range is **byte-identical in its canonical dump** (`OPERATIONAL_DATA.md` §9a) to the
  original, within the topic's retention.
- **AC-029 → FR-032** — Audit records (decisions, outcomes, mode transitions) are retained for the
  documented period and are queryable by equipment and time range. *(Scope stated 2026-09-30 by
  OD-011: proven in Phase 4 on schema-valid records through the real topics — a claim about storage,
  retention and query, not about which producer wrote them. Mode transitions carry `fromMode`,
  OD-019.)*
- **AC-030 → FR-050/051** — Every Operations API response validates against the OpenAPI contract,
  including error responses.
- **AC-031 → FR-052/NFR-006** — Demo fault-injection endpoints return 403 outside the demo profile
  and are **absent from the production-like build**.
- **AC-037 → NFR-006** — No dashboard build artifact contains an equipment-write call path.

## Observability and platform

- **AC-035 → NFR-003** — Every metric in the metrics contract is present with the documented type,
  unit, and label set.
- **AC-036 → NFR-003** — **No metric carries `equipmentId` as a label** (cardinality); per-equipment
  correlation is available in traces and exemplars.
- **AC-041 → NFR-003** — A single trace spans telemetry → prediction → decision → command with a
  continuous `correlationId`.
- **AC-042 → NFR-005** — Every published performance figure has a corresponding report under
  `reports/` carrying git commit, profile, machine specification, and configuration.
- **AC-043 → NFR-009** — The full baseline starts locally with containerised dependencies from a
  single documented command.
- **AC-044 → D-06** — Clock skew beyond 250 ms is detected and alerted.

## Explicit deferrals

| Requirement | Status | Reason |
|---|---|---|
| Manual/operator command origin | **Deferred** | Removed from baseline (ADR-0014); re-introduction requires its own AC set |
| Graded/reduced AI authority on OOD | **Deferred** | ADR-0015; requires measured evidence |
| Multi-broker Kafka HA | **Deferred** | `OPEN_DECISIONS` #7; single-broker local is not an HA claim |
| 3D digital twin | **Deferred** | ADR-0004 |
| Lakehouse / Spark / Airflow | **Deferred** | ADR-0007 |

## Coverage

Every P0 FR maps to at least one AC. FR-021's v0.2 ambiguity ("either … should support both if
feasible") is resolved: **both** failure probability and RUL are produced, both are nullable in the
contract, and AC-024 verifies the fields are present and populated for the baseline model
(GAP-098).
