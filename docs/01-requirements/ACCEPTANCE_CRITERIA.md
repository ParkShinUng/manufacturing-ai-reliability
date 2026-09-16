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
  `telemetry_sequence_gaps_total`; **no gap goes undetected** (D-03).

## Event platform

- **AC-003 → FR-011** — Replay a known Kafka range into **projection** consumer groups and verify
  read models rebuild identically, **and** that the Safety Supervisor does not re-issue any command
  (F11).
- **AC-026 → FR-010/012** — Every topic exists with the documented partition count, replication
  factor, and retention; the bootstrap job is idempotent and **asserts partition-count immutability**.
- **AC-027 → FR-011** — A schema-invalid record goes to the DLQ on the **first** attempt with all
  required DLQ headers; redrive is operator-initiated only.
- **AC-024 → FR-020/021/022** — Every prediction carries model name, version, run ID,
  `deploymentStage`, `featureSchemaVersion`, confidence, and `validSampleRatio`.
- **AC-025 → FR-021** — When `validSampleRatio < 0.8` on any safety-required channel, **no
  prediction is emitted at all** (rather than a low-confidence one).

## AI safety and control

- **AC-004 → FR-023/NFR-001** — Kill the inference process/pod; the Supervisor changes mode to
  fallback and the Control Service remains healthy.
- **AC-005 → FR-031/033** — Inject OOD, stale, and bad-quality telemetry and verify AI rejection
  with stable reason codes; OOD produces a **hard reject** (`OOD_HIGH`), never reduced authority.
- **AC-006 → FR-032/NFR-002** — Given a correlation ID, retrieve the full
  telemetry → prediction → safety-decision → command chain in a single query within 200 ms.
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
- **AC-028 → NFR-004** — A projection rebuilt from Kafka is byte-equivalent to the original.
- **AC-029 → FR-032** — Audit records (decisions, outcomes, mode transitions) are retained for the
  documented period and are queryable by equipment and time range.
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
