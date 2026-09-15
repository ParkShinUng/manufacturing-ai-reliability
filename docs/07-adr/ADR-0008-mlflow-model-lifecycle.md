# ADR-0008 — MLflow for experiment and model lifecycle
Status: Accepted

## Decision
Use MLflow for experiments, artifacts, registry state, and model traceability.

## Why
The project must prove model/version lineage and promotion/rollback discipline, not merely load a model file from disk.

## Consequences
Runtime must degrade gracefully if MLflow is unavailable after a Production model is already deployed.
