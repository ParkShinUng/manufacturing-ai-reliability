// Executable safety-configuration invariants SC-01..SC-10.
//
// Closes CODEX-R3-007: SAFETY_CONFIGURATION.md defines ten cross-field invariants that JSON Schema
// cannot express, and without an executable check an unsafe configuration would pass validation.
// The most important is SC-01 (commandTtlMs < inference cadence) - that inequality is the structural
// guarantee behind ADR-0013, not a tuning value, and a deployment that widens it silently
// reintroduces the command-reordering hazard.
//
// Each invariant is checked against the reference config AND against a deliberately mutated config,
// so a vacuously-passing check is itself a failure.
//
//   node tests/contract/safety_invariants.mjs

import { readFileSync } from "node:fs";

const REF = "contracts/examples/safety-config.demo.json";
const INFERENCE_CADENCE_MS = 5000;   // docs/02-architecture/RUNTIME_BEHAVIOR.md
const WATERMARK_INTERVAL_MS = 30000; // ADR-0019
const CHANNELS = 7;

const clone = (o) => JSON.parse(JSON.stringify(o));

/** Each invariant: id, why it exists, predicate, and a mutation that must break it. */
const INVARIANTS = [
  {
    id: "SC-01",
    why: "commandTtlMs must be < inference cadence, or two commands for one equipment can be concurrently valid and ADR-0013 collapses",
    ok: (c) => c.control.commandTtlMs < INFERENCE_CADENCE_MS,
    break: (c) => { c.control.commandTtlMs = 6000; },
  },
  {
    id: "SC-02",
    why: "supervisorSilenceTimeoutMs must exceed predictionTtlMs, or the watchdog fires while the Supervisor is still legitimately acting",
    ok: (c) => c.control.supervisorSilenceTimeoutMs > c.defaults.predictionTtlMs,
    break: (c) => { c.control.supervisorSilenceTimeoutMs = 8000; },
  },
  {
    id: "SC-03",
    why: "deadmanTimeoutMs must exceed supervisorSilenceTimeoutMs, so L3 acts only after L2 has also failed",
    ok: (c) => c.control.deadmanTimeoutMs > c.control.supervisorSilenceTimeoutMs,
    break: (c) => { c.control.deadmanTimeoutMs = 5000; },
  },
  {
    id: "SC-04",
    why: "fallback rate must be reachable within AI authority, or fallback is unreachable by the mechanism that must reach it",
    ok: (c) => every(c, (k) => k.aiAuthorizedMinPct <= k.fallbackRatePct && k.fallbackRatePct <= k.aiAuthorizedMaxPct),
    break: (c) => { cls(c).fallbackRatePct = 30.0; },
  },
  {
    id: "SC-05",
    why: "AI authority must sit inside the physical envelope",
    ok: (c) => every(c, (k) => k.absoluteMinRatePct <= k.aiAuthorizedMinPct && k.aiAuthorizedMaxPct <= k.absoluteMaxRatePct),
    break: (c) => { cls(c).aiAuthorizedMaxPct = 120.0; },
  },
  {
    id: "SC-06",
    why: "a single decision must not be able to exceed the whole rolling-window budget",
    ok: (c) => every(c, (k) => k.maxDeltaPerDecisionPp <= k.rateBudgetPp),
    break: (c) => { cls(c).maxDeltaPerDecisionPp = 40.0; },
  },
  {
    id: "SC-07",
    why: "watermark timeout must tolerate two lost watermarks, or publisher liveness alarms on noise",
    ok: (c) => c.authorization.watermarkTimeoutMs >= 3 * WATERMARK_INTERVAL_MS,
    break: (c) => { c.authorization.watermarkTimeoutMs = 45000; },
  },
  {
    id: "SC-08",
    why: "every equipment must reference a declared class; a dangling reference means undefined bounds",
    ok: (c) => c.equipment.every((e) => e.class in c.equipmentClasses),
    break: (c) => { c.equipment[0].class = "does-not-exist"; },
  },
  {
    id: "SC-09",
    why: "safety-required and advisory channels must be disjoint and together cover all 7, or quality.overall derivation is undefined",
    ok: (c) => every(c, (k) => {
      const s = new Set(k.safetyRequiredChannels), a = new Set(k.advisoryChannels);
      const overlap = [...s].some((x) => a.has(x));
      return !overlap && new Set([...s, ...a]).size === CHANNELS;
    }),
    break: (c) => { cls(c).advisoryChannels.push("rpm"); },
  },
  {
    id: "SC-10",
    why: "clock skew budget must not dominate the tightest freshness gate",
    ok: (c) => c.defaults.clockSkewBudgetMs < c.defaults.telemetryFreshnessMs / 4,
    break: (c) => { c.defaults.clockSkewBudgetMs = 900; },
  },
];

const cls = (c) => c.equipmentClasses[Object.keys(c.equipmentClasses)[0]];
function every(c, f) { return Object.values(c.equipmentClasses).every(f); }

const ref = JSON.parse(readFileSync(REF, "utf8"));
let failed = 0;

// Extra structural check: every deployed equipment must be listed (no implicit defaults).
if (!Array.isArray(ref.equipment) || ref.equipment.length === 0) {
  console.error("FAIL: equipment list is empty; a missing entry must be invalid config, never an implicit default");
  failed++;
}

for (const inv of INVARIANTS) {
  const holds = inv.ok(ref);
  if (!holds) {
    console.error(`FAIL ${inv.id}: reference config violates it\n       ${inv.why}`);
    failed++;
    continue;
  }
  // Negative case: the mutation MUST break the invariant, or the check is vacuous.
  const mutated = clone(ref);
  inv.break(mutated);
  if (inv.ok(mutated)) {
    console.error(`FAIL ${inv.id}: check is VACUOUS - the mutated config still passes`);
    failed++;
    continue;
  }
  console.log(`ok   ${inv.id}  (holds on reference, and the mutation is correctly rejected)`);
}

console.log(`\n${INVARIANTS.length} invariants checked, ${failed} failing.`);
process.exit(failed ? 1 : 0);
