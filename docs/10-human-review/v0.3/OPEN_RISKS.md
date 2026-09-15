# Open Risks — v0.3

> Risks that remain after the dual-agent review. **Agreement between two agents is not the same as
> absence of risk**, so risks are listed even where both agents converged.

| ID | Risk | Sev | Prob | Impact | Mitigation | Residual | Phase | Human attention |
|---|---|---|---|---|---|---|---|---|
| R-01 | **Every performance figure is an unmeasured TARGET.** A reviewer could mistake a target for a result. | HIGH | HIGH | credibility | Every value labelled `TARGET (unmeasured)`; NFR-011 forbids publishing unmeasured figures; AC-042 requires a report per published figure | Low once Phase 12 runs | 12 | **YES** |
| R-02 | **The 12 s watchdog and 30 s dead-man are derived, not tuned.** Derivation is sound (TTL + gRPC budget + margin) but unvalidated against real timing. | MED | MED | spurious fallback, or slow reaction | Values are configuration, not constants; SC-02/SC-03 invariants enforce ordering; FAIL-SUP-001 measures actual detection time | Low | 7 | no |
| R-03 | **Single-instance services.** The platform demonstrates *recovery*, not high availability. | MED | HIGH | misread as an HA claim | ADR-0005 and `OPEN_DECISIONS` #7 state it explicitly; local Kafka is RF=1 and is never presented as HA | Low if stated honestly | 10 | **YES** |
| R-04 | **`mlops-publisher` is a new safety-relevant component** introduced to fix DEC-009. | MED | LOW | quarantine fails to propagate | Watermark makes its death detectable in 90 s and fails closed; F28; AC-034 | Low | 9 | no |
| R-05 | **Simulator physics are plausible, not validated.** Constants define our twin; they are not measurements of a real machine. | LOW | HIGH | unrealistic scenarios | Stated explicitly in `EQUIPMENT_MODEL_AND_STATE.md` §0; never presented as real-world limits | Low | 1 | no |
| R-06 | **Toolchain versions need re-verification** at Phase 1 init (.NET 10.0.401, Python 3.13.15, Kafka 4.3.1, K8s 1.36.x, PostgreSQL 18, Node 24 LTS). | LOW | MED | build friction | `TOOLCHAIN.md` requires digest pinning and verification at repository initialisation | Low | 1 | no |

## Post-decision status (2026-09-15)

| ID | Item | State |
|---|---|---|
| R-08 | `APPROVED_WITH_CONDITIONS` recorded with no conditions stated | **CLOSED** — conditions C-1/C-2/C-3 stated and satisfied, with evidence in `HUMAN_APPROVAL.md` |

**R-01 is now the risk that matters most.** With the gate open, the temptation to present this work
as more proven than it is grows rather than shrinks: every performance figure is still an unmeasured
target, and AC-011/AC-012 — the tests that prove the headline claim — cannot run until Phase 7.

## Deliberately accepted, not risks

| Item | Why accepted |
|---|---|
| No manual operator control | ADR-0014; HD-002 |
| No graded AI authority on OOD | ADR-0015; untestable at this scale |
| No multi-broker HA | `OPEN_DECISIONS` #7; not an HA claim |
| No Lakehouse/Spark/Airflow | ADR-0007; no requirement demands them |
| Bounded telemetry loss during a Kafka outage | D-02; loss is counted and flagged. **Undetected** loss (D-03) remains a hard zero |

## The honest summary

The largest residual risk is **R-01**: this specification is thorough, but every number in it is a
target. The architecture is designed so the claims are *testable*; none of them are *tested* yet. Any
presentation of this work should say so plainly.
