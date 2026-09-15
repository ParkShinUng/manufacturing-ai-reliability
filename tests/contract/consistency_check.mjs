// Documentation consistency checker (v0.3, the STEP-26 consistency pass made executable).
//
// Catches the class of defect that produced GAP-001, GAP-011, GAP-025 and CODEX-R2-001:
// the same concept spelled two ways in two documents, or a normative list duplicated.
//
//   node tests/contract/consistency_check.mjs
// Exit 0 = consistent. Exit 1 = at least one violation.

import { readFileSync, readdirSync, statSync } from "node:fs";
import { join } from "node:path";

const roots = ["docs", "contracts", "MASTER_SPEC.md", "README.md", "AGENTS.md", "CLAUDE.md", "CHANGELOG.md"];
const files = [];
function walk(p) {
  const st = statSync(p);
  if (st.isDirectory()) for (const f of readdirSync(p)) walk(join(p, f));
  else if (/\.(md|json|yaml|proto)$/.test(p)) files.push(p);
}
for (const r of roots) { try { walk(r); } catch {} }

const read = (f) => readFileSync(f, "utf8");
const violations = [];
const v = (rule, file, msg) => violations.push({ rule, file, msg });

// Warnings surface information the build must NOT fail on - typically something only a human can
// resolve. Keeping them distinct stops the checker from crying wolf on a field an agent must not fill.
const warnings = [];
const warn = (rule, file, msg) => warnings.push({ rule, file, msg });

// ---- 1. Canonical topic names are plural. Catches GAP-011 / CODEX-R2-001.
const badTopics = [
  ["factory.prediction.v1", "factory.predictions.v1"],
  ["factory.safety-decision.v1", "factory.safety-decisions.v1"],
  ["factory.equipment-state.v1", "factory.equipment-states.v1"],
  ["factory.control-outcome.v1", "factory.control-outcomes.v1"],
  ["factory.model-deployment.v1", "factory.model-deployments.v1"],
];
for (const f of files) {
  const s = read(f);
  for (const [bad, good] of badTopics) {
    const re = new RegExp(bad.replace(/\./g, "\\."), "g");
    let m;
    while ((m = re.exec(s))) {
      const line = s.slice(0, m.index).split("\n").length;
      v("topic-name", `${f}:${line}`, `"${bad}" should be "${good}"`);
    }
  }
}

// ---- 2. A removed reason code must not reappear as a live code.
for (const f of files) {
  const s = read(f);
  if (s.includes("RATE_CHANGE_LIMITED") && !/Removed in v0\.3|superseded|GAP-033|removed and split/i.test(s)) {
    v("removed-code", f, "RATE_CHANGE_LIMITED is removed (GAP-033); use RATE_CHANGE_CLAMPED or RATE_BUDGET_EXHAUSTED");
  }
}

// ---- 3. The OOD contradiction must not return.
for (const f of files) {
  const s = read(f);
  if (/reduce AI authority/.test(s) && !/Removed in v0\.3|v0\.2|deferred|DEC-005|ADR-0015/i.test(s)) {
    v("ood-semantics", f, 'OOD must be a hard reject (ADR-0015); "reduce AI authority" is removed');
  }
}

// ---- 4. Self-asserted command source must not return to the command contract.
{
  const p = "contracts/proto/control/v1/control.proto";
  const proto = read(p);
  if (/^\s*string source\s*=/m.test(proto)) {
    v("command-source", p, "`source` must not be a request field; origin is server-derived (ADR-0016)");
  }
  if (!/authenticated_source/.test(proto)) {
    v("command-source", p, "missing `authenticated_source` on the response (ADR-0016 audit requirement)");
  }
}

// ---- 5. Manual command origin must stay removed from the baseline.
for (const f of files) {
  const s = read(f);
  if (/explicit human\/manual mode/.test(s) && !/removed|Deferred|DEC-004|ADR-0014|v0\.2/i.test(s)) {
    v("manual-mode", f, "manual command origin is removed from the v0.3 baseline (ADR-0014)");
  }
}

// ---- 6. Exactly one normative safety gate table.
{
  const canonical = "docs/04-ai/AI_SAFETY_AND_MLOPS.md";
  if (!/Canonical safety gate table/i.test(read(canonical))) {
    v("gate-table", canonical, "canonical gate table missing");
  }
  if (!/canonical, normative gate list lives in/i.test(read("MASTER_SPEC.md"))) {
    v("gate-table", "MASTER_SPEC.md", "MASTER_SPEC must REFERENCE the canonical gate table, not duplicate it");
  }
}

// ---- 7. MANUAL_OPERATOR must not appear as a live control mode.
for (const f of files) {
  const s = read(f);
  if (/MANUAL_OPERATOR/.test(s) && !/deliberately|rejected|Deferred|DEC-004|ADR-0014|absent|excluded|No MANUAL_OPERATOR|withdrawn/i.test(s)) {
    v("control-mode", f, "MANUAL_OPERATOR is not a v0.3 baseline mode (ADR-0014)");
  }
}

// ---- 8. Every schema a document references must exist.
const schemaFiles = new Set(readdirSync("contracts/jsonschema/v1"));
for (const f of files.filter((x) => x.endsWith(".md"))) {
  const s = read(f);
  for (const m of s.matchAll(/([a-z-]+\.schema\.json)/g)) {
    if (!schemaFiles.has(m[1])) v("missing-schema", f, `references ${m[1]} which does not exist`);
  }
}

// ---- 9. Referenced service-design docs must exist. Catches CODEX-R2-002.
const svc = new Set(readdirSync("docs/11-service-design"));
for (const f of files.filter((x) => x.endsWith(".md"))) {
  const s = read(f);
  for (const m of s.matchAll(/docs\/11-service-design\/([A-Z_]+\.md)/g)) {
    if (!svc.has(m[1])) v("missing-service-doc", f, `references ${m[1]} which does not exist`);
  }
}

// ---- 10. Safety-configuration ordering invariants (SC-01, SC-02).
{
  const p = "docs/03-contracts/SAFETY_CONFIGURATION.md";
  const cfg = read(p);
  const silence = /supervisorSilenceTimeoutMs:\s*(\d+)/.exec(cfg);
  const cmdTtl = /commandTtlMs:\s*(\d+)/.exec(cfg);
  const predTtl = /predictionTtlMs:\s*(\d+)/.exec(cfg);
  if (silence && predTtl && Number(silence[1]) <= Number(predTtl[1])) {
    v("invariant-SC-02", p, `supervisorSilenceTimeoutMs (${silence[1]}) must exceed predictionTtlMs (${predTtl[1]})`);
  }
  if (cmdTtl && Number(cmdTtl[1]) >= 5000) {
    v("invariant-SC-01", p, `commandTtlMs (${cmdTtl[1]}) must be < inference cadence 5000ms, or concurrent command validity returns`);
  }
}

// ---- 11. v0.2 leftovers: statements the v0.3 decisions invalidated.
//        These rules exist because the first consistency pass PASSED while ten v0.2 documents
//        still contradicted v0.3 - the checker only looked for what its author had suspected.
const STALE = [
  [/Owns gate logic and reason codes\./, "control mode ownership moved to Control Service (ADR-0011); restate what the Supervisor owns"],
  [/`safety\.decision`/, "non-canonical topic name; use factory.safety-decisions.v1"],
  [/8 bounded components/i, "there are 9 components in v0.3 (mlops-publisher added, ADR-0019)"],
];
for (const f of files.filter((x) => x.endsWith(".md"))) {
  const s = read(f);
  for (const [re, msg] of STALE) {
    if (re.test(s) && !/v0\.2|Removed in v0\.3|superseded/i.test(s.slice(Math.max(0, s.search(re) - 200), s.search(re) + 200))) {
      v("v02-leftover", f, msg);
    }
  }
}

// ---- 12. Documents that MUST acknowledge v0.3, or they are silently stale.
const MUST_REFERENCE_V03 = [
  "docs/02-architecture/SYSTEM_ARCHITECTURE.md",
  "docs/02-architecture/RUNTIME_BEHAVIOR.md",
  "docs/02-architecture/REPOSITORY_STRUCTURE.md",
  "docs/00-product/GLOSSARY.md",
  "docs/06-development/DEVELOPMENT_PROTOCOL.md",
  "docs/06-development/DEFINITION_OF_DONE.md",
  "docs/06-development/TEST_STRATEGY.md",
  "docs/06-development/CODING_STANDARDS.md",
];
for (const f of MUST_REFERENCE_V03) {
  let s;
  try { s = read(f); } catch { v("stale-doc", f, "file missing"); continue; }
  if (!/v0\.3|ADR-001[1-9]|DUAL_AGENT_PROTOCOL|controlEpoch|mlops-publisher|DEFINITION_OF_READY|TEST_SPECIFICATIONS/.test(s)) {
    v("stale-doc", f, "no v0.3 reference - this document was left behind and may contradict the new decisions");
  }
}

// ---- 13. Required process documents exist.
for (const f of [
  "docs/06-development/DEFINITION_OF_READY.md",
  "docs/06-development/DUAL_AGENT_PROTOCOL.md",
  "docs/06-development/TEST_SPECIFICATIONS.md",
  "docs/10-human-review/v0.3/HUMAN_APPROVAL.md",
]) {
  try { read(f); } catch { v("missing-process-doc", f, "required by the v0.3 process and absent"); }
}

// ---- 14. Roadmap must carry all 13 phases (0-12).
{
  const plan = read("docs/08-roadmap/IMPLEMENTATION_PLAN.md");
  for (let i = 0; i <= 12; i++) {
    // NOTE: build this with concatenation, not a template literal. `\b` inside a template
    // literal is the BACKSPACE character, not a regex word boundary - the first version of this
    // rule reported all 13 phases missing when all 13 were present.
    if (!new RegExp("## Phase " + i + "\\b").test(plan)) {
      v("roadmap", "docs/08-roadmap/IMPLEMENTATION_PLAN.md", `Phase ${i} missing`);
    }
  }
}

// ---- 15. Pending human decisions must be visibly marked wherever their value appears.
{
  const f = "contracts/examples/safety-config.demo.json";
  const s = read(f);
  if (/maxAuthorizationStalenessMs/.test(s) && !/HD-001/.test(s)) {
    v("unapproved-value", f, "maxAuthorizationStalenessMs traces to HD-001; keep the decision reference so a copied config cannot lose its provenance");
  }
}

// ---- 16. The approval record must carry a decision, and must never be set by an agent.
{
  const f = "docs/10-human-review/v0.3/HUMAN_APPROVAL.md";
  const s = read(f);
  // Built from a string, not a regex literal: a literal newline cannot appear inside /.../ .
  const m = new RegExp("## Status\\s*\\n+\\*\\*([A-Z_]+)\\*\\*").exec(s);
  if (!m) v("approval", f, "status not parseable");
  else if (!["PENDING", "APPROVED", "APPROVED_WITH_CONDITIONS", "REQUEST_CHANGES", "REJECTED"].includes(m[1])) {
    v("approval", f, `unknown status "${m[1]}"`);
  }
  // A status whose meaning depends on a field the human has not filled in.
  // WARNING, not a violation: only the product owner can resolve these, and failing the build on a
  // field an agent must not fill would be noise rather than signal.
  const blank = /\(to be completed by the product owner\)/.test(s);

  if (m && (m[1] === "REJECTED" || m[1] === "REQUEST_CHANGES") && blank) {
    warn("approval-reason", f,
      `${m[1]} recorded without a reason or intended direction. Specification work cannot be ` +
      `correctly scoped until the product owner states it, and no agent may guess at it.`);
  }

  // APPROVED_WITH_CONDITIONS authorizes Phase 1 "once the conditions are satisfied", which cannot
  // be evaluated while the conditions are blank. The failure mode this guards against is an empty
  // field being read later as "no conditions" and implementation starting on an ungranted approval.
  if (m && m[1] === "APPROVED_WITH_CONDITIONS" && blank) {
    warn("approval-conditions", f,
      "APPROVED_WITH_CONDITIONS recorded with no conditions stated. Until they are written down " +
      "the effective gate is unchanged from PENDING, and an empty conditions field must NEVER be " +
      "read as \"no conditions\".");
  }
}

// ---- 17. The retired `aegis` naming must not come back.
//        Retired 2026-09-15: `aegis` collides with 7+ GitHub projects (4 AI-adjacent), is taken on
//        PyPI, and carries an unrelated defence-system connotation. Replaced by the repo slug
//        `manufacturing-ai-reliability` and the code token `mair`.
// A historical record is allowed to name what was retired - that is what a changelog IS. The rule
// only fires on a LIVE use: an occurrence with no retirement context within 300 chars around it.
const HISTORY_OK = /retired|renamed|was `?aegis|formerly|collision|CHANGELOG|historical|old name|구 이름/i;
for (const f of files) {
  if (f.replace(/\\/g, "/").endsWith("CHANGELOG.md")) continue;
  const s = read(f);
  const re = /aegis/gi;
  let m;
  while ((m = re.exec(s))) {
    const ctx = s.slice(Math.max(0, m.index - 300), m.index + 300);
    if (HISTORY_OK.test(ctx)) continue;
    const line = s.slice(0, m.index).split("\n").length;
    v("retired-name", `${f}:${line}`,
      'retired naming "aegis" in live use; the repo slug is `manufacturing-ai-reliability` and the ' +
      "code token is `mair` (proto / python / .NET / SPIFFE / $id / URN)");
  }
}

// ---- 18. The code token must stay consistent across every identifier context.
//         A rename that updates prose but leaves one identifier behind reads as abandoned branding,
//         which is exactly the failure Codex flagged when this rename was designed.
{
  const expect = [
    ["contracts/proto/control/v1/control.proto", /^package mair\.control\.v1;/m, "proto package must be mair.control.v1"],
    ["docs/03-contracts/OT_PROTOCOL_MAPPING.md", /urn:mair:equipment:v1/, "OPC UA namespace URI must be urn:mair:equipment:v1"],
  ];
  for (const [f, re, msg] of expect) {
    try { if (!re.test(read(f))) v("token-drift", f, msg); }
    catch { v("token-drift", f, "file missing"); }
  }
  // every JSON Schema $id must share one host
  for (const sf of readdirSync("contracts/jsonschema/v1")) {
    if (!sf.endsWith(".json")) continue;
    const id = JSON.parse(read(join("contracts/jsonschema/v1", sf))).$id || "";
    if (id && !id.startsWith("https://mair.local/")) {
      v("token-drift", `contracts/jsonschema/v1/${sf}`, `$id host must be mair.local, found "${id}"`);
    }
  }
}

// ---- report
function printGroup(list, label, stream) {
  const byRule = {};
  for (const x of list) (byRule[x.rule] ||= []).push(x);
  for (const [rule, items] of Object.entries(byRule)) {
    stream(`
[${label}: ${rule}] ${items.length}`);
    for (const x of items) stream(`  ${x.file}
    ${x.msg}`);
  }
}

if (warnings.length) printGroup(warnings, "warning", (m) => console.log(m));

if (violations.length === 0) {
  console.log(`
Consistency check passed. ${files.length} files scanned, 0 violations, ${warnings.length} warning(s).`);
  process.exit(0);
}
const byRule = {};
for (const x of violations) (byRule[x.rule] ||= []).push(x);
for (const [rule, list] of Object.entries(byRule)) {
  console.error(`\n[${rule}] ${list.length} violation(s)`);
  for (const x of list) console.error(`  ${x.file}\n    ${x.msg}`);
}
console.error(`\n${files.length} files scanned, ${violations.length} violations.`);
process.exit(1);
