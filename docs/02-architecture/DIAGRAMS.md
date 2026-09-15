# Architecture Diagrams

> Closes GAP-095. All diagrams are Mermaid and must agree with the contracts and written
> architecture; a diagram that disagrees with a contract is a defect in the diagram.
> The equipment and control-mode state machines live with their transition tables in
> `EQUIPMENT_MODEL_AND_STATE.md` §3.5 and `CONTROL_MODE_STATE_MACHINE.md` §6 and are not duplicated
> here — two copies of a state machine is the same defect as two copies of a gate list.

## 1. System context

```mermaid
flowchart TB
    OP["Operator"]
    ML["ML / MLOps engineer"]
    SRE["Platform / SRE reviewer"]

    subgraph MAIR["Manufacturing AI Reliability Platform"]
        DASH["Operations Dashboard"]
        API["Operations API"]
        PLAT["Platform services"]
        SIM["Equipment Simulator / HIL"]
    end

    MLF["MLflow"]

    OP --> DASH --> API --> PLAT
    PLAT --> SIM
    ML --> MLF --> PLAT
    SRE --> PLAT
```

## 2. Service architecture with authority layers

```mermaid
flowchart TB
    subgraph OT["OT zone"]
        SIM["equipment-simulator<br/>L3 self-protection"]
    end
    subgraph EDGE["Edge zone"]
        GW["edge-gateway<br/>READ-ONLY to equipment"]
        SUP["safety-supervisor<br/>L1 advisory"]
        CTL["control-service<br/>L2 SOLE application writer"]
    end
    subgraph DATA["Data / AI zone"]
        K["Kafka"]
        PRED["prediction-service"]
        TRAIN["training"]
        PUB["mlops-publisher"]
        MLF["MLflow"]
        PG["PostgreSQL"]
    end
    subgraph UI["Presentation zone"]
        OPS["operations-service"]
        DASH["operations-dashboard"]
    end

    SIM -->|"OPC UA / Modbus (read)"| GW
    GW -->|telemetry, equipment-states| K
    K --> PRED
    PRED -->|predictions| K
    K --> SUP
    SUP -->|"gRPC mTLS<br/>ApplyCommand + lease"| CTL
    CTL -->|"OPC UA / Modbus (WRITE)"| SIM
    CTL -->|control-outcomes| K
    SUP -->|safety-decisions| K
    K --> OPS --> PG
    OPS --> DASH
    TRAIN --> MLF --> PUB -->|model-deployments| K
    K --> SUP

    classDef crit fill:#ffe0e0,stroke:#c00,stroke-width:2px
    class CTL,SUP,SIM crit
```

**The command path is the only red edge that writes.** Note that Kafka never appears on it — that is
ADR-0009 made visual.

## 3. Telemetry flow

```mermaid
sequenceDiagram
    participant EQ as Equipment
    participant GW as edge-gateway
    participant K as Kafka
    participant FB as feature-builder
    participant PROJ as operations-projector

    EQ->>GW: sample @100ms (+ quality, sequence)
    GW->>GW: normalise, range-check, flag, derive quality.overall
    Note over GW: BAD quality => value null<br/>NEVER a substituted value
    GW->>K: factory.telemetry.v1 (key=equipmentId)
    K->>FB: consume
    FB->>FB: 1s aggregates, nulls EXCLUDED, validSampleRatio
    K->>PROJ: consume -> PostgreSQL
```

## 4. Prediction → safety → control (happy path)

```mermaid
sequenceDiagram
    participant PRED as prediction-service
    participant K as Kafka
    participant SUP as safety-supervisor
    participant CTL as control-service
    participant EQ as Equipment

    PRED->>K: prediction (stage, featureSchemaVersion, validSampleRatio)
    K->>SUP: consume
    SUP->>SUP: 13 gates in order; record ALL results
    SUP->>K: safety-decision (ACCEPT + RATE_CHANGE_CLAMPED)
    SUP->>CTL: ApplyCommand(controlEpoch, expires=+2s)
    CTL->>CTL: verify mTLS identity -> authenticatedSource
    CTL->>CTL: epoch check, expiry, supersession, bounds, budget
    CTL->>EQ: write setpoint
    EQ-->>CTL: ack
    CTL->>K: control-outcome (APPLIED)
```

## 5. AI failure → fallback

```mermaid
sequenceDiagram
    participant PRED as prediction-service
    participant SUP as safety-supervisor
    participant CTL as control-service
    participant EQ as Equipment

    Note over PRED: process killed
    PRED--xSUP: no predictions
    SUP->>SUP: gate 3 PREDICTION_FRESH fails after 10s
    SUP->>CTL: ApplyCommand(fallback 60%)
    CTL->>EQ: write 60%
    Note over SUP,CTL: mode M3 -> SAFE_FALLBACK<br/>Control Service healthy throughout (AC-004)
```

## 6. Supervisor failure → watchdog → epoch fencing

```mermaid
sequenceDiagram
    participant SUP as safety-supervisor
    participant CTL as control-service
    participant EQ as Equipment

    SUP->>CTL: ApplyCommand @T0 (epoch=41) [DELAYED IN NETWORK]
    Note over SUP: process killed at T0
    SUP--xCTL: heartbeats stop
    Note over CTL: silence 12s (= TTL 10s + gRPC budget 1.4s + margin)
    CTL->>CTL: epoch 41 -> 42 (COMMIT BEFORE WRITE)
    CTL->>EQ: write fallback 60%
    CTL->>CTL: mode M4 -> SAFE_FALLBACK
    SUP-->>CTL: delayed command arrives @T+16s (epoch=41)
    CTL-->>SUP: FENCED / COMMAND_EPOCH_STALE
    Note over CTL,EQ: equipment stays at 60%.<br/>Without fencing this command<br/>would have undone the fallback.
```

This is the race Codex found in Round 1; the diagram exists because it is the single most important
interaction in the platform.

## 7. Kafka failure

```mermaid
flowchart LR
    EQ["Equipment"] --> GW["edge-gateway"]
    GW -->|"buffer 6000<br/>drop OLDEST<br/>never block"| BUF[("bounded buffer")]
    BUF -.->|Kafka down| K["Kafka"]
    SUP["safety-supervisor"] -.->|no predictions| FB["gate 3 fails<br/>SAFE_FALLBACK"]
    CTL["control-service"] -->|"gRPC - unaffected"| EQ2["Equipment"]

    classDef ok fill:#e0ffe0,stroke:#080
    class CTL,EQ2 ok
```

**Kafka is CONTROL-INDEPENDENT (F06).** The OT poll loop never blocks, because blocking it would make
the analytics backbone a dependency of the control-adjacent path.

## 8. Model lifecycle and authorization

```mermaid
flowchart LR
    EXP["Experiment"] --> REG["Registered"] --> CAND["Candidate"]
    CAND --> SH["Shadow<br/>no authority"] --> CAN["Canary<br/>explicit cohort"] --> PROD["Production"]
    PROD --> ARCH["Archived"]
    PROD --> QUAR["Quarantined"]
    CAN --> QUAR

    MLF["MLflow"] --> PUB["mlops-publisher"]
    PUB -->|"acks=all BEFORE<br/>reporting complete"| TOPIC["factory.model-deployments.v1<br/>compacted"]
    PUB -->|"watermark every 30s"| TOPIC
    TOPIC --> SUP["safety-supervisor<br/>NEVER calls MLflow"]
    SUP -->|"watermark age > 90s"| UNAUTH["all models unauthorized<br/>-> fallback"]
```

## 9. Local vs production-like deployment

```mermaid
flowchart TB
    subgraph LOCAL["Local (docker compose)"]
        L1["1 KRaft broker, RF=1, min.insync=1"]
        L2["private network + per-service token"]
        L3["OPC UA security: None"]
        L4["not an HA claim"]
    end
    subgraph PROD["Production-like (Kubernetes)"]
        P1["3 brokers, RF=3, min.insync=2"]
        P2["mTLS everywhere"]
        P3["Basic256Sha256 SignAndEncrypt"]
        P4["probes, rollout, failure demos"]
    end
    LOCAL -->|"ADR-0005: local correctness first"| PROD
```

## 10. Trust boundaries

```mermaid
flowchart TB
    subgraph Z1["OT simulation zone"]
        SIM["simulator<br/>ONE writable node per equipment"]
    end
    subgraph Z2["Edge zone"]
        GW["gateway - READ-ONLY session"]
        CTL["control-service - THE write identity"]
        SUP["supervisor - NO equipment credentials"]
    end
    subgraph Z3["Data / AI zone"]
        PRED["prediction - NO equipment credentials"]
        TRAIN["training - NO equipment credentials"]
    end
    subgraph Z4["Presentation zone"]
        API["operations-api"]
        DASH["dashboard - NO write path exists"]
    end

    GW -->|read| SIM
    CTL -->|"WRITE (only identity granted)"| SIM
    SUP -->|"gRPC mTLS, no OT creds"| CTL
    Z3 -.->|"no path to equipment"| Z1
    DASH --> API
    API -.->|"no route reaches control-service"| CTL

    classDef nocred fill:#eef,stroke:#66a
    class PRED,TRAIN,DASH,SUP nocred
```

The dotted edges are the important ones: they are paths that **must not exist**, and each is asserted
by a test (AC-037, AC-039).
