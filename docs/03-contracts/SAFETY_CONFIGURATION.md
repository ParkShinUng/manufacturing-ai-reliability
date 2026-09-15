# Safety Configuration Contract

> Closes GAP-029. v0.2 described fallback as "deterministic and configuration-backed … per equipment
> class" with no schema, owner, reload behaviour, or invalid-config behaviour — so the deterministic
> path was not, in fact, deterministically implementable. Found by Codex.
> Status: v0.3 normative.

## 1. Ownership and handling

| Aspect | Rule |
|---|---|
| **Owner** | **Control Service** — it must be able to act when the Supervisor is gone (ADR-0011) |
| Storage | version-controlled file, mounted **read-only** |
| Format | YAML, validated against `contracts/jsonschema/v1/safety-config.schema.json` |
| Validation | **at startup, fail-closed** — invalid config **aborts startup** |
| Defaults | **none.** A missing equipment entry is invalid config, not an implicit default |
| Reload | explicit operator-triggered only; **never** a filesystem watcher |
| Reload validation | validated before swap; on failure the previous valid config is retained and an alert is raised |
| Consumers | Control Service (authoritative); Safety Supervisor reads the same file for gate bounds |

**Why fail-closed at startup rather than defaulting.** A service that silently starts with a guessed
fallback rate is more dangerous than one that refuses to start, because the guess is invisible. A
refusal is loud, immediate, and cannot reach equipment. This is NFR-014.

**Why no filesystem watcher.** A watcher turns an editor's temporary write, a partial copy, or a
half-saved file into a live safety-configuration change. Reload must be a deliberate act.

## 2. Schema

```yaml
version: 1

defaults:                          # applied only where an equipment class omits a value;
                                   # NEVER applied to a missing equipment entry
  fallbackRatePct: 60.0
  aiAuthorizedMinPct: 60.0
  aiAuthorizedMaxPct: 100.0
  maxDeltaPerDecisionPp: 10.0
  rateBudgetPp: 25.0
  rateBudgetWindowSec: 60
  telemetryFreshnessMs: 2000
  predictionTtlMs: 10000
  clockSkewBudgetMs: 250

control:
  supervisorSilenceTimeoutMs: 12000      # ADR-0011; = predictionTtl + gRPC budget + margin
  commandTtlMs: 2000                     # ADR-0013; MUST be < inferenceCadenceMs
  leaseDurationMs: 30000
  heartbeatIntervalMs: 3000
  idempotencyRetentionMin: 15
  credentialRotationGraceSec: 300
  deadmanTimeoutMs: 30000                # ADR-0011; L3, must exceed supervisorSilenceTimeoutMs

authorization:
  watermarkTimeoutMs: 90000              # ADR-0019; 3 missed 30s watermarks
  maxAuthorizationStalenessMs: 60000     # HD-001 - APPROVED by product owner 2026-09-15

equipmentClasses:
  conveyor-drive-v1:
    absoluteMinRatePct: 0.0
    absoluteMaxRatePct: 100.0
    fallbackRatePct: 60.0
    safetyRequiredChannels: [vibrationRms, temperatureC, currentA, rpm, operationRatePct]
    advisoryChannels: [torqueNm, voltageV]

equipment:
  - id: eq-001
    class: conveyor-drive-v1
  # ... every equipment MUST be listed. An unlisted equipment is invalid config.
```

## 3. Cross-field invariants

Validated at startup; any violation aborts.

| ID | Invariant | Why |
|---|---|---|
| SC-01 | `commandTtlMs < inferenceCadenceMs` | the structural guarantee behind ADR-0013 — violating it reintroduces concurrent command validity |
| SC-02 | `supervisorSilenceTimeoutMs > predictionTtlMs + gRPC budget` | otherwise the watchdog fires while the Supervisor is still legitimately acting |
| SC-03 | `deadmanTimeoutMs > supervisorSilenceTimeoutMs` | L3 must act only after L2 has also failed |
| SC-04 | `aiAuthorizedMinPct <= fallbackRatePct <= aiAuthorizedMaxPct` | fallback must be reachable within AI authority |
| SC-05 | `absoluteMin <= aiAuthorizedMin` and `aiAuthorizedMax <= absoluteMax` | AI authority must sit inside the physical envelope |
| SC-06 | `maxDeltaPerDecisionPp <= rateBudgetPp` | a single decision must not exceed the window budget |
| SC-07 | `watermarkTimeoutMs >= 3 × watermarkIntervalMs` | tolerate two lost watermarks without a false alarm |
| SC-08 | every `equipment[].class` exists in `equipmentClasses` | no dangling class reference |
| SC-09 | `safetyRequiredChannels` and `advisoryChannels` are disjoint and together cover all 7 channels | otherwise `quality.overall` derivation is undefined |
| SC-10 | `clockSkewBudgetMs` < smallest freshness threshold ÷ 4 | skew must not dominate a gate decision |

SC-01 deserves emphasis: it is not a tuning parameter but the precondition that makes
arrival-order command processing safe. A deployment that widens `commandTtlMs` past the inference
cadence silently reintroduces the reordering hazard that ADR-0013 exists to eliminate, which is why
it is a startup-abort invariant rather than a warning.

## 4. Failure behaviour

| Situation | Behaviour |
|---|---|
| File missing | **startup abort** |
| Unparseable | **startup abort** |
| Schema violation | **startup abort**, reporting every violation, not just the first |
| Invariant violation | **startup abort**, naming the invariant ID |
| Equipment listed in config but absent from the platform | warn, continue |
| Equipment present but absent from config | **startup abort** — never an implicit default |
| Reload with invalid config | retain previous valid config, alert, **do not apply** |
| Reload during `STOP_REQUIRED` | **rejected** — configuration must not change under an active stop |

## 5. Change control

Safety configuration changes follow the documentation-first workflow: they are reviewed like code,
carry a rationale, and a change to any value governed by an SC invariant requires the invariant to be
re-verified. `maxAuthorizationStalenessMs` was set to **60 000 ms by human decision HD-001 (2026-09-15)**. It is
now a decided value, not a proposal. Changing it requires a new human decision, because it bounds how
long a possibly-quarantined model may retain control authority when Kafka is unreachable.

## 6. Verification

The SC invariants are **executable now**, before any application code exists:

```bash
node tests/contract/safety_invariants.mjs
```

It checks each invariant twice: once against the reference config (`contracts/examples/safety-config.demo.json`),
and once against a deliberately mutated config that **must** violate it. A vacuously-passing check is
itself reported as a failure, so an invariant cannot silently stop testing anything.

- **AC-040** — invalid configuration aborts startup rather than starting with defaults.
- A test asserts that a config omitting one deployed equipment aborts startup.
- The startup validator in Control Service must implement the same ten checks; the script above is
  the specification-phase proof that they are well-formed and mutually satisfiable.
