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
| **Read-only port** | `5020` — telemetry. **Every write function code is refused with exception `0x01` (Illegal Function).** This is the port the Edge Gateway uses. |
| **Write port** | `5021` — the single holding-register write (§2.4), plus the same read access. This is the port the Control Service uses. |
| Unit ID | `equipmentIndex + 1` (1–247) |
| Byte order | **big-endian** (Modbus network order) |
| 32-bit word order | **high word first** (big-endian word order) |
| Encoding | scaled 32-bit signed integers across two registers |

Word order is the classic Modbus interoperability trap and is stated explicitly because roughly half
of real devices do the opposite.

#### Why two ports (OD-003, resolved 2026-09-16)

Modbus TCP has **no authentication, no session identity, and no per-client permission model**. A
connection carries a unit id, a function code and an address; there is no principal. So the
OPC UA mechanism — provision the gateway's session without write permission — has no Modbus
equivalent, and until this was resolved the contract asserted an enforcement that could not exist.

Two listeners restore the property **inside the software**, with a precisely bounded scope. On
`5020` there is no code path that writes, so **a gateway connected to `5020` cannot write no matter
what defect or compromise it suffers** — true by construction rather than by trust, and provable by
an acceptance test rather than by inspecting a firewall.

It does **not** make the gateway incapable of writing in general. A gateway misconfigured to `5021`
can write, and only network policy prevents that. What the split changes is the failure mode: from
"any gateway bug can write" to "only a wrong port can write". A port is a reviewable configuration
value; a bug is not. Achieving the same thing with a single listener would require the network layer
to filter by Modbus **function code**, which an IP-and-port policy cannot do without deep packet
inspection.

Both ports serve **identical telemetry**; they differ only in whether write function codes exist.
Non-privileged ports are used because `502` requires root and buys nothing in a container.

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

### 2.4 Holding register (function code **16 only**) — the single write

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

**This section describes the write-capable listener `5021` only.** On the read-only listener `5020`
every write function code is refused with exception `0x01` (Illegal Function) before addressing is
considered, so `5020` never returns any of the codes below — one malformed write cannot have two
different answers depending on which port it arrived on.

**Function code 16 only, quantity exactly 2, starting exactly at the base.** *Corrected 2026-09-16;
this section previously said "function code 6/16", which is not implementable.* `FC06` writes a
single 16-bit register, and `operationRateSetpointPct` is a **two-register int32**, so `FC06` cannot
express this field at all — it could only ever write half of it. A half-written setpoint is not a
malformed request that gets rejected later; it is a **different, arbitrary rate** that the equipment
would act on, which is the failure mode this whole address map exists to prevent.

| Request | Response |
|---|---|
| `FC16`, start = base, quantity = 2, value 0–1000 | accepted |
| `FC16`, start = base, quantity = 2, value outside 0–1000 | exception `0x03` Illegal Data Value, **neither register mutated** |
| `FC16` with quantity ≠ 2, or not starting at the base | exception `0x02` Illegal Data Address |
| `FC06`, or any write function code other than `FC16`, against holding-register space | exception `0x02` Illegal Data Address |
| any write function code to the input-register space (§2.3) | exception `0x02` Illegal Data Address |
| any write function code on the read-only listener `5020` | exception `0x01` Illegal Function |

The rejection must leave **both** registers unchanged. A library whose server writes into a buffer
before the value is visible to the application cannot satisfy this row, which is what decided
ADR-0020.

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

Obligation 9 is the protocol-level statement of FR-035. **How it is enforced differs by protocol,
and this section previously claimed more than Modbus TCP can deliver** (corrected 2026-09-16, raised
by the ADR-0020 challenge).

**OPC UA — enforced by the server.** The gateway and the Control Service hold *different* OT
credentials (`SECURITY_BOUNDARIES.md`), and the gateway's session is provisioned without write
permission on `OperationRateSetpointPct`. Obligation 9 is enforced rather than trusted.

**Modbus TCP — enforced by listener separation, because credentials do not exist.** Modbus TCP has
**no authentication, no session identity, and no per-client permission model**. There is no
credential to provision and nothing for the server to check, so the previous sentence — which
asserted a single mechanism for both protocols — was not implementable.

Resolved as **OD-003 option A**: the simulator exposes two listeners (§2.1). The gateway connects to
the **read-only** port `5020`, which refuses every write function code with exception `0x01`; the
Control Service connects to `5021`. Obligation 9 therefore holds on the Modbus side by construction,
not by trusting the gateway, and `AC-039` can prove it.
