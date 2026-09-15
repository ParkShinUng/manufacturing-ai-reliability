# ADR-0010 — Shadow evaluation before model canary authority
Status: Accepted

## Decision
Candidate models first consume the same telemetry in an independent shadow consumer group and publish candidate predictions without control authority. After offline/shadow gates pass, canary authority may be enabled only for an explicit set of simulator equipment IDs.

## Why
Industrial AI rollout should observe candidate behavior before allowing even bounded influence. Equipment-cohort canary is easier to audit than random request percentage for stateful time-series equipment.

## Consequences
Prediction events identify `deploymentStage` and model version. Safety Supervisor accepts only the authorized stage/cohort.
