# ADR-0007 — No Lakehouse/Spark/Airflow in baseline
Status: Accepted

## Context
These are valuable enterprise data-platform tools but can become resume-driven architecture without a data-volume/workflow requirement.

## Decision
Do not include them in baseline. Add only when a specific retention, analytics, batch orchestration, or scale requirement justifies them.

## Why
Demonstrates architectural restraint and keeps attention on reliability, OT, AI safety, and MLOps.
