# OT Protocol Mapping — OPC UA and Modbus TCP

> Closes: GAP-045. Satisfies FR-003 (telemetry available through OPC UA and at least one Modbus TCP
> mapping) and FR-005 (gateway normalises protocol-specific values into canonical telemetry).
> Status: v0.3 normative. This is a **contract** — the simulator implements the server side, the
> gateway implements the client side, and neither may deviate without a contract change.

## 0. Why both protocols

FR-003 requires both. They are not redundant: OPC UA carries **typed values with native quality and
source timestamps**, while Modbus TCP carries **raw 16-bit registers with no type, no quality, and
no timestamp**. The gateway must therefore synthesise quality and timestamps for Modbus and
faithfully translate them for OPC UA. That asymmetry is the point — it is what makes the
normalisation layer (FR-005) a real piece of engineering rather than a field rename.

---

## 1. OPC UA mapping

### 1.1 Server identity

| Item | Value |
|---|---|
| Endpoint | `opc.tcp://{simulator-host}:4840/mair/` |
| Namespace URI | `urn:mair:equipment:v1` |
| Namespace index | resolved at runtime; **never hard-coded** |
| Security policy (demo) | `None` |
| Security policy (production-like) | `Basic256Sha256`, `SignAndEncrypt` |

The namespace index must be resolved by URI at session start. Hard-coding index `2` is the single
most common OPC UA integration defect and is explicitly forbidden here.

### 1.2 Address space

Per equipment instance, under `Objects/Equipment/{equipmentId}`:

| BrowseName | NodeId (string ident.) | OPC UA type | Access | Maps to |
|---|---|---|---|---|
| `Rpm` | `ns=<n>;s=Eq.{id}.Rpm` | `Double` | R | `measurements.rpm` |
| `TorqueNm` | `ns=<n>;s=Eq.{id}.TorqueNm` | `Double` | R | `measurements.torqueNm` |
| `CurrentA` | `ns=<n>;s=Eq.{id}.CurrentA` | `Double` | R | `measurements.currentA` |
| `VoltageV` | `ns=<n>;s=Eq.{id}.VoltageV` | `Double` | R | `measurements.voltageV` |
| `TemperatureC` | `ns=<n>;s=Eq.{id}.TemperatureC` | `Double` | R | `measurements.temperatureC` |
| `VibrationRms` | `ns=<n>;s=Eq.{id}.VibrationRms` | `Double` | R | `measurements.vibrationRms` |
| `OperationRatePct` | `ns=<n>;s=Eq.{id}.OperationRatePct` | `Double` | R | `measurements.operationRatePct` |
| `EquipmentState` | `ns=<n>;s=Eq.{id}.State` | `UInt16` (enum) | R | equipment state (§3) |
| `SequenceNo` | `ns=<n>;s=Eq.{id}.SequenceNo` | `UInt64` | R | `sequence` |
| **`OperationRateSetpointPct`** | `ns=<n>;s=Eq.{id}.RateSetpoint` | `Double` | **R/W** | `SET_OPERATION_RATE` target |

`OperationRateSetpointPct` is the **only writable node in the entire address space**. This is the OPC
UA-level expression of ADR-0002 and FR-035: there is physically nothing else for a rogue writer to
write.

### 1.3 Subscription parameters

| Parameter | Value | Reason |
|---|---|---|
| Publishing interval | 100 ms | matches telemetry cadence (`RUNTIME_BEHAVIOR.md`) |
| Sampling interval | 50 ms | Nyquist-safe against the 100 ms publish |
| Queue size | 10 | absorbs one publish-cycle stall without loss |
| Discard oldest | `true` | prefer fresh data; loss is flagged, not hidden |
| Deadband | none (absolute 0) | a deadband would silently suppress `SENSOR_FROZEN` detection |
| Keep-alive count | 5 | ~500 ms silence detection |
| Lifetime count | 15 | 3× keep-alive, per OPC UA convention |

### 1.4 Quality translation

OPC UA `StatusCode` maps to canonical quality (DEC-008 vocabulary):

| StatusCode | `quality.overall` | Flag | Value |
|---|---|---|---|
| `Good` (0x00000000) | `GOOD` | — | as read |
| `Good_LocalOverride` | `UNCERTAIN` | `OUTLIER_SUSPECTED` | as read |
| `Uncertain_*` (0x40000000–) | `UNCERTAIN` | `STALE_READING` | as read |
| `Bad_NoCommunication` | `BAD` | `SENSOR_DISCONNECTED` | **`null`** |
| `Bad_OutOfService` | `BAD` | `SENSOR_MISSING` | **`null`** |
| `Bad_SensorFailure` | `BAD` | `SENSOR_MISSING` | **`null`** |
| any other `Bad_*` (0x80000000–) | `BAD` | `SENSOR_MISSING` | **`null`** |

A `Bad_*` status **must** produce `null`, never a last-known or zero value (DEC-008). Substituting a
value here would feed the safety gates a fabricated reading.

### 1.5 Timestamps

The gateway uses the OPC UA **SourceTimestamp** as `eventTimeUtc` when present, falling back to
ServerTimestamp, and records its own receipt clock as `ingestTimeUtc`. If SourceTimestamp is absent
on all monitored items, the gateway sets `eventTimeUtc = ingestTimeUtc` and raises
`TIMESTAMP_SYNTHESISED` — because silently pretending the gateway clock is the source clock is what
makes freshness gates lie (GAP-060).

---

## 2. Modbus TCP mapping

### 2.1 Server identity

| Item | Value |
|---|---|
| Port | `5020` (non-privileged; `502` requires root and buys nothing in a container) |
| Unit ID | `equipmentIndex + 1` (1–247) |
| Byte order | **big-endian** (Modbus network order) |
| 32-bit word order | **high word first** (big-endian word order) |
| Encoding | scaled 32-bit signed integers across two registers |

Word order is the classic Modbus interoperability trap and is stated explicitly because roughly half
of real devices do the opposite.

### 2.2 Why scaled integers, not IEEE-754 floats

Modbus has no native float. Two encodings are possible: two registers as IEEE-754, or a scaled
integer. This contract uses **scaled integers** because the scale factor is explicit in the contract
and therefore checkable, whereas a float encoding hides a word-order mistake as a plausible-looking
wrong number. A wrong scale factor produces an obviously wrong magnitude; a wrong word order in a
float produces garbage that can still pass a range check.

### 2.3 Input registers (function code 4) — read-only telemetry

Base address per equipment: `40 × equipmentIndex`.

| Offset | Registers | Field | Encoding | Scale | Engineering value |
|---|---|---|---|---|---|
| +0 | 2 | `rpm` | int32 | ×10 | `raw / 10.0` rpm |
| +2 | 2 | `torqueNm` | int32 | ×10 | `raw / 10.0` N·m |
| +4 | 2 | `currentA` | int32 | ×100 | `raw / 100.0` A |
| +6 | 2 | `voltageV` | int32 | ×10 | `raw / 10.0` V |
| +8 | 2 | `temperatureC` | int32 | ×10 | `raw / 10.0` °C |
| +10 | 2 | `vibrationRms` | int32 | ×100 | `raw / 100.0` mm/s |
| +12 | 2 | `operationRatePct` | int32 | ×10 | `raw / 10.0` % |
| +14 | 1 | `equipmentState` | uint16 | — | state enum (§3) |
| +15 | 1 | `qualityBitmap` | uint16 | — | per-channel validity, §2.5 |
| +16 | 4 | `sequence` | uint64 | — | high word first |
| +20 | 2 | `sourceEpochMs` | uint32 | — | **milliseconds** since equipment start (occupies +20 and +21). Wraps at 2^32 ms (~49.7 days); a wrap is indistinguishable from a restart, so the simulator MUST also reset `sequence` on wrap so the two signals stay consistent. |
| +22 | 1 | `statusBitmap` | uint16 | — | bit 0 = fault injection active (demo profile only); bits 1–15 reserved, must be 0. Distinct from `qualityBitmap` (§2.5). |

### 2.4 Holding register (function code 6/16) — the single write

Base address per equipment: **`4 × equipmentIndex`** in the holding-register space. Without a
per-equipment base every machine would alias the same write address.

| Offset | Registers | Field | Encoding | Scale | Range |
|---|---|---|---|---|---|
| +0 | 2 | `operationRateSetpointPct` | int32 | ×10 | 0–1000 (= 0.0–100.0 %) |
| +2 | 2 | reserved | — | — | must read 0 |

Again: **one** writable location per equipment. A write outside 0–1000 is rejected by the simulator
with Modbus exception `0x03` (Illegal Data Value) and is **not** clamped — silently clamping an
out-of-range write would hide a control-path defect that the Control Service bounds check is
supposed to catch first.

### 2.5 Quality bitmap (register +15)

Modbus carries no quality, so it is synthesised. Bit set = channel **valid**.

| Bit | Channel |
|---|---|
| 0 | `rpm` |
| 1 | `torqueNm` |
| 2 | `currentA` |
| 3 | `voltageV` |
| 4 | `temperatureC` |
| 5 | `vibrationRms` |
| 6 | `operationRatePct` |
| 7–15 | reserved (must be 0) |

This register encodes **channel validity only**. Simulator fault-injection status is a distinct
concern and is exposed separately in input register **+22** (`statusBitmap`, bit 0 = fault injection
active, demo profile only), so a status bit can never be misread as a validity bit.

> Register **+22**, not +21: `sourceEpochMs` at +20 is a 32-bit value and therefore occupies **both**
> +20 and +21. Placing the status bitmap at +21 would have aliased it onto the high word of the
> timestamp.

A clear bit means the gateway must emit `null` for that channel with `SENSOR_MISSING`, exactly as for
an OPC UA `Bad_*` status. Fault-injection status lives in register **+22** (`statusBitmap` bit 0), not here, so a failure
test can assert that the *gateway* detected a fault rather than the harness asserting against its
own injection command - without a status bit ever being mistakable for a validity bit.

### 2.6 Timestamps under Modbus

There is no source timestamp. The gateway sets `eventTimeUtc = ingestTimeUtc` and **always** raises
`TIMESTAMP_SYNTHESISED` for Modbus-sourced telemetry. Consequence, which must be stated: telemetry
freshness for Modbus equipment measures *gateway-to-consumer* latency only, not
*sensor-to-consumer* latency. `sourceEpochMs` (register +20) allows the gateway to detect a frozen
or restarted device, which is the one thing it can honestly infer.

### 2.7 Polling

| Parameter | Value |
|---|---|
| Poll interval | 100 ms |
| Read | one block read of **23 registers** (offsets +0…+22) per equipment |
| Response timeout | 250 ms |
| Reconnect backoff | 250 ms → 8 s, exponential ×2, ±20 % jitter |

One block read per equipment, not seven single-register reads: it is atomic with respect to the
simulator's update cycle, so a poll cannot straddle two model steps and return a physically
inconsistent sample.

---

## 3. Equipment state enum (shared by both protocols)

| Value | State |
|---|---|
| 0 | `OFFLINE` |
| 1 | `CONNECTING` |
| 2 | `IDLE` |
| 3 | `RUNNING` |
| 4 | `DEGRADED` |
| 5 | `FAULT` |
| 6 | `STOPPING` |

Values are frozen. Appending is allowed; renumbering is a breaking change requiring a new contract
version.

---

## 4. Normalisation obligations on the gateway (FR-005)

For every telemetry event the gateway must:

1. resolve the OPC UA namespace index by URI, never by hard-coded index;
2. apply the scale factors in §2.3 for Modbus; reject any value outside the §1.2 valid range of
   `EQUIPMENT_MODEL_AND_STATE.md` with `VALUE_OUT_OF_RANGE` and emit `null`;
3. translate protocol quality into the closed canonical flag vocabulary (DEC-008);
4. emit `null` — never a substituted value — for any channel whose source quality is bad;
5. derive `quality.overall` from the safety-required/advisory mapping in
   `EQUIPMENT_MODEL_AND_STATE.md` §4;
6. set `eventTimeUtc` from the source timestamp where one exists, else synthesise it and raise
   `TIMESTAMP_SYNTHESISED`;
7. set `ingestTimeUtc` from its own clock, and raise `TIMESTAMP_REVERSED` if
   `eventTimeUtc > ingestTimeUtc` beyond the configured skew budget (GAP-063);
8. carry `sequence` through unmodified from the source and raise `SEQUENCE_GAP` on discontinuity;
9. never originate an equipment write. The gateway is **read-only** toward equipment; the writable
   setpoint node/register is addressed exclusively by the Control Service.

Obligation 9 is the protocol-level statement of FR-035, and it is worth noting that the gateway and
the Control Service therefore hold *different* OT credentials (`SECURITY_BOUNDARIES.md`): the
gateway's session must be provisioned without write permission, so that obligation 9 is enforced by
the server rather than trusted of the client.
