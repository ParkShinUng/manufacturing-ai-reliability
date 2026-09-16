# Service Design — `edge-gateway`

> Runtime: C#/.NET 10 Worker on Linux. Owns the OT↔platform boundary.
> Closes GAP-090 for this service; implements DEC-008 quality derivation.

## 1. Purpose
Translate protocol-specific equipment data into the canonical telemetry contract, with honest
quality and timestamps, and publish it to Kafka. The asymmetry between OPC UA (typed, with quality
and source timestamps) and Modbus (raw registers, none of those) is what makes this a real
normalisation layer rather than a field rename.

## 2. Responsibilities
OPC UA + Modbus client sessions; reconnect without process restart (FR-004); normalisation (FR-005);
**closed-vocabulary quality flags and derived `quality.overall`** (DEC-008, sole owner);
timestamp assignment and anomaly flagging; `sequence` gap detection; bounded buffering;
Kafka production; **gateway-observed equipment state** to `factory.equipment-states.v1`.

## 3. Non-responsibilities
**Never writes to equipment** — server-enforced, not trusted (FR-035). On OPC UA the session is
provisioned without write permission; on Modbus the gateway connects to the **read-only listener
`5020`**, which refuses every write function code with exception `0x01`, because Modbus TCP has no
identity to withhold permission from (OD-003). No AI. No control decisions. Does not own equipment
ground truth, only its observation.

## 4. Dependencies
| Dependency | Class | Failure behaviour |
|---|---|---|
| Equipment protocols | Control-adjacent | infinite reconnect, capped backoff; state → `OFFLINE` |
| Kafka | **Non-control-critical** | buffer, drop oldest; **never blocks the OT poll loop** |

The second row is the architectural crux: blocking the poll loop on a Kafka outage would make the
analytics backbone a dependency of the OT path, which MASTER_SPEC principle 4 forbids.

## 5. Inputs / outputs
**In:** OPC UA subscriptions, Modbus polls. **Out:** canonical telemetry and equipment-state records
across the **egress port** (§5.1), and metrics. In the completed system the egress port is bound to
`factory.telemetry.v1` and `factory.equipment-states.v1`.

### 5.1 The egress port (Phase 2 / Phase 3 boundary)

The gateway emits canonical records through a narrow egress interface rather than calling a Kafka
producer inline. **This is not a new abstraction introduced for testing** — it is the seam §4 and
§13 already require: Kafka is classified `Non-control-critical` and "never blocks the OT poll loop",
and §13 says blocking is forbidden. A bounded buffer between the poll loop and the transport is
what makes that true, and a buffer has two ends. Naming the far end is implementing the design.

| Phase | What is proven |
|---|---|
| **2** | the gateway **emits** canonical records conforming to `telemetry.schema.json`, with bounded buffering and drop-oldest, against a recording egress sink. AC-001, AC-021, AC-022, AC-023 |
| **3** | the egress port is **bound to Kafka** under the topology contract — topic creation, pinned partitions, retention, idempotent producer, DLQ and replay. FR-010, AC-026, AC-027 |

Split this way because **AC-001 maps to FR-001/FR-006, not FR-010**. Kafka publication is FR-010 and
is Phase 3's subject. Reading the schema's topic binding as a Phase 2 requirement conflates the
contract's *data shape* with its *transport realisation*, and would have dragged partition counts,
retention and producer settings into Phase 2 where an implementer would have chosen them instead of
`KAFKA_TOPOLOGY_AND_SEMANTICS.md`.

## 6. Contracts
`contracts/jsonschema/v1/telemetry.schema.json`, `equipment-state.schema.json`;
`docs/03-contracts/OT_PROTOCOL_MAPPING.md`.

## 7. Data ownership
Owns: normalised telemetry, quality flags, `quality.overall`, `ingestTimeUtc`, `stateSequence`,
observed state, buffer. Carries through unmodified: `sequence`, source timestamps.

## 8. State model
Per equipment: session state, last `sequence`, last values (frozen detection), trailing 60 s stats
(outlier detection), `stateSequence`, observed state, buffer depth.

## 9. Lifecycle
Load config → resolve OPC UA namespace **by URI** (never a hard-coded index) → establish sessions →
subscribe / begin polling → start Kafka producer → ready.

## 10. Normal flow
```text
on sample:
  1  map protocol value -> canonical channel (scale factors for Modbus)
  2  range-check -> VALUE_OUT_OF_RANGE + null on violation
  3  translate protocol quality -> closed flag vocabulary; BAD => value null
  4  frozen detection (30 identical samples; operationRatePct exempt)
  5  outlier detection (>6 sigma over trailing 60s)
  6  timestamps: source or synthesised; reversal check
  7  sequence gap check
  8  derive quality.overall from safety-required vs advisory map
  9  enqueue (bounded 6000, drop oldest)
  10 produce to Kafka
```
Step 3's "BAD ⇒ null" is absolute: substituting a last-known or zero value would feed the safety
gates a fabricated reading.

## 11. Failure behaviour
| Failure | Behaviour |
|---|---|
| Protocol disconnect | state → `OFFLINE`; reconnect 250 ms→8 s ±20 %; **no process restart** (FR-004) |
| Kafka down | buffer; drop oldest; `BUFFER_OVERFLOW_DROP` on the next event; poll loop continues |
| Buffer overflow | bounded, counted, flagged — never silent |
| Sensor bad | `null` + flag; never substituted |
| Sequence gap | `SEQUENCE_GAP` + `telemetry_sequence_gaps_total` |
| Clock skew | `TIMESTAMP_REVERSED` beyond budget |

## 12. Timeout / retry / idempotency / ordering
OPC UA: publish 100 ms, keep-alive 5. Modbus: poll 100 ms, response timeout 250 ms, **one block read
of 23 registers** (`+0`…`+22`) per equipment so a poll cannot straddle two model steps.
*Corrected 2026-09-16: this said 22, left over from before `CODEX-R3-005` moved `statusBitmap` to
`+22` to stop it aliasing the high word of the 32-bit `sourceEpochMs`. A 22-register read would have
silently never returned `statusBitmap`. `OT_PROTOCOL_MAPPING.md` §2.3 is authoritative (DEC-007).* Producer:
`enable.idempotence=true`, `acks=all`, in-flight ≤ 5, key = `equipmentId`.

## 13. Backpressure
Capacity 6 000 (~30 s at demo scale); drop oldest; **blocking forbidden**. Buffering beyond the
freshness horizon buys nothing, since stale telemetry is rejected by the gates anyway.

## 14. Restart recovery
Sequence tracking restarts; first sample after restart raises `SEQUENCE_GAP` unless `sourceEpochMs`
also reset. In-flight buffer is lost — bounded, documented loss (F03), not a silent one.

## 15. Configuration
Endpoints, `equipmentIds`, `pollIntervalMs` (100), `bufferCapacity` (6 000), `frozenThreshold` (30),
`outlierSigma` (6), `clockSkewBudgetMs` (250), reconnect backoff, Kafka settings.

## 16. Security
Read-only toward equipment, by two different mechanisms (OD-003):

| Protocol | Mechanism |
|---|---|
| OPC UA | session provisioned **without** write permission. Production-like: `Basic256Sha256` / `SignAndEncrypt` |
| Modbus TCP | connects **only** to the read-only listener `5020`. There are no Modbus credentials to provision, so the guarantee is structural: that listener has no write code path |

No equipment write credentials exist in this service's environment. The gateway must be configured
with `5020` and never `5021`.

What the listener split buys is narrower than "the gateway can never write", and the difference
matters: **given the correct port, no defect or compromise in this service can produce an equipment
write**, because the connection it holds carries no write function codes at all. It does **not**
survive a misconfiguration to `5021` — a gateway pointed there can write, and only network policy
stops it. That is why the port is a reviewed configuration value rather than a default.

## 17. Observability
Label sets are the ones in `OBSERVABILITY_AND_SLO.md`, which is authoritative; they are repeated
here only so this design is readable on its own.

`protocol_connected{protocol}`, `telemetry_events_total{protocol}`,
`telemetry_publish_errors_total{reason}`, `telemetry_dropped_total`, `local_buffer_depth`,
`reconnect_total{protocol}`, `telemetry_sequence_gaps_total`, `quality_flags_total{flag,channel}`,
`clock_skew_seconds`.

Every label is a closed enum — `quality_flags_total` is bounded at 12 flags × 8 channels — and
`equipmentId` is never a label (`CODING_STANDARDS.md`).

## 18. Performance targets (TARGET — unmeasured)
20 equipment × 10 Hz = 200 ev/s sustained; 250 equipment = 2 500 ev/s;
P95 normalise+publish ≤ 20 ms; reconnect ≤ 10 s after endpoint restore.

## 19. Test strategy
Unit: scaling, word order, quality translation, frozen/outlier/gap detection, `overall` derivation
(a fixture-driven test asserting the derived value for every flag combination).
Integration: simulator↔gateway over both protocols producing identical canonical output.
Failure: AC-002 reconnect; Kafka outage buffering; buffer overflow.

## 20. Acceptance criteria
AC-001, AC-002, AC-021, AC-022, AC-023.
