#!/usr/bin/env node
// AC traceability: every acceptance criterion must map to a test specification, or be explicitly
// listed as pending against a phase that has not started.
//
// TEST_SPECIFICATIONS.md §7 claimed this was "asserted by a test rather than maintained by hand".
// It was not — the check did not exist, and when it was finally written it found 25 of 44 criteria
// unmapped, including AC-039, which had been assigned to no phase at all while verifying two P0
// requirements.
//
// Dependency-free, like the other checks here, so it runs in CI without a toolchain.

import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const read = (p) => readFileSync(join(root, p), "utf8");

const CRITERIA = "docs/01-requirements/ACCEPTANCE_CRITERIA.md";
const SPECS = "docs/06-development/TEST_SPECIFICATIONS.md";
const PLAN = "docs/08-roadmap/IMPLEMENTATION_PLAN.md";

const criteria = read(CRITERIA);
const specs = read(SPECS);
const plan = read(PLAN);

// An AC is *declared* by its definition line: "- **AC-001 → FR-.../...**". A mention elsewhere is a
// cross-reference, not a declaration.
const declared = [...criteria.matchAll(/^-\s+\*\*(AC-\d{3})\s*(?:→|->)/gm)].map((m) => m[1]);

if (declared.length === 0) {
  console.error(`Found no AC declarations in ${CRITERIA}. The parser and the document have diverged,`);
  console.error("which is worse than a missing mapping: the check would pass silently forever.");
  process.exit(1);
}

const duplicates = declared.filter((id, i) => declared.indexOf(id) !== i);
const ids = [...new Set(declared)];

// §7a is the list of specifications NOT yet written, so it must be cut out before scanning for
// coverage. Leaving it in was the checker's first bug: every pending AC counted as covered by the
// very row that says it is not, and the check passed while proving nothing.
const [beforePending, afterPending = ""] = specs.split(/^##\s+7a\.[^\n]*$/m);
const pendingSection = afterPending.split(/^##\s+\d/m)[0] ?? "";
const coverageText = beforePending + afterPending.slice(pendingSection.length);

// A specification "covers" an AC if it names it anywhere outside §7a: a heading, an Asserts column,
// a table row.
const covered = new Set([...coverageText.matchAll(/AC-\d{3}/g)].map((m) => m[0]));

// "AC-018 through AC-023" covers everything between its endpoints.
for (const m of coverageText.matchAll(/(AC-(\d{3}))\s*(?:through|–|—)\s*(AC-(\d{3}))/g)) {
  for (let n = Number(m[2]); n <= Number(m[4]); n++) {
    covered.add(`AC-${String(n).padStart(3, "0")}`);
  }
}

// Phase status from the roadmap headings. COMPLETE and IN PROGRESS both mean "started", so any
// specification owed by that phase is due now.
const startedPhases = new Set();
for (const m of plan.matchAll(/^##\s+Phase\s+(\d+)\b([^\n]*)$/gm)) {
  if (/COMPLETE|IN PROGRESS/i.test(m[2])) startedPhases.add(Number(m[1]));
}

// §7a rows: "| AC-026, AC-027 | 3 |".
const pending = new Map();
for (const row of pendingSection.matchAll(/^\|\s*((?:AC-\d{3}\s*,?\s*)+)\|\s*(\d+)\s*\|/gm)) {
  for (const id of row[1].match(/AC-\d{3}/g) ?? []) pending.set(id, Number(row[2]));
}

const unmapped = ids.filter((id) => !covered.has(id) && !pending.has(id));
const unknown = [...covered].filter((id) => !ids.includes(id));
const orphanPending = [...pending.keys()].filter((id) => !ids.includes(id));
const stale = [...pending.keys()].filter((id) => covered.has(id));
const overdue = [...pending.entries()].filter(([, phase]) => startedPhases.has(phase));

const problems = [];

if (duplicates.length) {
  problems.push([`${CRITERIA} declares an AC more than once`, [...new Set(duplicates)]]);
}
if (unmapped.length) {
  problems.push([
    `declared in ${CRITERIA}, named by no specification, and not listed as pending in §7a`,
    unmapped,
  ]);
}
if (unknown.length) {
  problems.push([`named in ${SPECS} but declared nowhere in ${CRITERIA}`, unknown]);
}
if (orphanPending.length) {
  problems.push([`listed as pending in §7a but declared nowhere in ${CRITERIA}`, orphanPending]);
}
if (stale.length) {
  problems.push([
    "listed as pending although a specification now names them — remove the §7a row",
    stale,
  ]);
}
if (overdue.length) {
  // The rule that stops §7a becoming a permanent excuse.
  problems.push([
    "listed as pending, but their phase has started — the specification is due",
    overdue.map(([id, phase]) => `${id} (phase ${phase})`),
  ]);
}

if (problems.length === 0) {
  const mapped = ids.length - pending.size;
  console.log(
    `\nAC traceability passed. ${ids.length} criteria: ${mapped} mapped to a specification, ` +
    `${pending.size} pending in phases that have not started.`,
  );
  process.exit(0);
}

for (const [heading, list] of problems) {
  console.error(`\n[ac-traceability] ${list.length} — ${heading}`);
  for (const id of [...list].sort()) console.error(`  ${id}`);
}

console.error(
  `\n${ids.length} criteria checked. An unmapped AC is a build failure, not a documentation gap:` +
  "\nthe criterion is the promise, and nothing is holding it.",
);
process.exit(1);
