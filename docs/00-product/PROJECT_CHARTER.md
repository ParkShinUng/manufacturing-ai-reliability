# Project Charter

## Problem
Industrial AI can detect degradation earlier than fixed alarms, but a production system must also handle incorrect predictions, stale data, network loss, messaging outages, model regressions, and inference-process failure.

## Product objective
Build a portfolio-grade but production-minded platform that demonstrates how AI can be introduced into manufacturing operations **without making AI part of the minimum safe-control dependency chain**.

## Target users
- manufacturing software engineer / OT engineer
- equipment operator
- reliability/maintenance engineer
- ML/MLOps engineer
- platform/SRE reviewer

## Primary use case
A set of simulated machines emits industrial telemetry. The platform predicts anomaly/failure/RUL and may recommend bounded operating-rate changes. A deterministic Safety Supervisor validates the recommendation and either accepts a bounded command or rejects it and keeps/returns the equipment to a documented fallback policy.

## Success criteria
The portfolio is successful when a reviewer can answer, from evidence:
- What fails when AI dies? **Not control availability.**
- What happens when data is stale or OOD? **AI is rejected.**
- Can events be replayed and traced? **Yes, with IDs and offsets.**
- Can a bad model be detected and rolled back? **Yes, demonstrated.**
- Why was each major technology chosen? **ADRs explain it.**
- What happens when the **safety supervisor itself** dies? **Control Service drives to fallback in
  12 s; equipment self-reverts at 30 s.** (v0.3, ADR-0011 - v0.2 had no answer to this.)
- Can a delayed command undo a fallback? **No - epoch fencing rejects it.**
- Can a replayed prediction move equipment? **No - three independent mechanisms prevent it.**
- How was each decision challenged? **Decision Records show the adversarial exchange.**
- Are performance claims reproducible? **Load/failure scripts produce them.**
