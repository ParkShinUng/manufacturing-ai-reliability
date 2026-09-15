# ADR-0005 — Local container baseline before Kubernetes
Status: Accepted

## Decision
All core services must first be runnable locally with containers. Kubernetes manifests/Helm are added after local integration is stable.

## Why
This separates application correctness from orchestration complexity while still enabling a production-platform portfolio phase.

## Consequences
Two deployment profiles must remain consistent; configuration must be environment-driven.
