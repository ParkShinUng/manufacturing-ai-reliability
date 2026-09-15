# ADR-0002 — AI cannot directly control equipment
Status: Accepted

## Context
Model error, OOD input, stale data, inference outage, and model regression are expected production risks.

## Decision
AI emits recommendations only. Safety Supervisor validates them. Control Service is the sole equipment-write owner.

## Why
This isolates probabilistic AI from deterministic command authority, enables explicit rejection reasons, and keeps fallback available when AI fails.

## Consequences
Adds components and latency, but provides auditable authority boundaries and a stronger reliability story.
