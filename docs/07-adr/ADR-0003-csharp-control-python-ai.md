# ADR-0003 — C#/.NET for OT/control, Python for ML
Status: Accepted

## Context
The project needs industrial protocol handling and deterministic application logic as well as ML experimentation/serving.

## Decision
Use C#/.NET for simulator, gateway, safety, and control. Use Python for training/inference.

## Why
Each runtime is used where its ecosystem and project experience are strongest. Runtime separation also isolates AI failure from control components.

## Consequences
Requires versioned language-neutral contracts and cross-runtime observability.
