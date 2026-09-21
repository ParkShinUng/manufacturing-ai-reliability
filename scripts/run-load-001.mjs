#!/usr/bin/env node
// LOAD-001 driver: 20 equipment at 100 ms through the real protocol stack, in wall-clock time.
//
// NFR-005 and AC-042 require every performance figure to be produced by a script and recorded with
// its environment, never typed by hand. This is that script. It captures the commit, machine and
// toolchain, runs the scenario, and writes a report under reports/load/.
//
//   node scripts/run-load-001.mjs [--seconds N]
//
// The full criterion is 30 minutes. A shorter run is allowed for smoke-testing the harness, and the
// report says so in its own title rather than letting a two-minute run be mistaken for LOAD-001.

import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { cpus, totalmem, platform, release, arch } from "node:os";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const REQUIRED_SECONDS = 30 * 60;

const argIndex = process.argv.indexOf("--seconds");
const seconds = argIndex > -1 ? Number(process.argv[argIndex + 1]) : REQUIRED_SECONDS;

if (!Number.isFinite(seconds) || seconds <= 0) {
  console.error("--seconds must be a positive number");
  process.exit(1);
}

const run = (cmd, args) =>
  execFileSync(cmd, args, { cwd: root, encoding: "utf8", maxBuffer: 64 * 1024 * 1024 });

const quiet = (cmd, args) => {
  try {
    return run(cmd, args).trim();
  } catch {
    return "unavailable";
  }
};

// Environment first: a measurement without the machine that produced it is not reproducible, and
// the runbook requires commit, profile, machine specification, duration and configuration.
const environment = {
  commit: quiet("git", ["rev-parse", "HEAD"]),
  branch: quiet("git", ["rev-parse", "--abbrev-ref", "HEAD"]),
  dirty: quiet("git", ["status", "--porcelain"]).length > 0,
  dotnet: quiet("dotnet", ["--version"]),
  node: process.version,
  os: `${platform()} ${release()} ${arch()}`,
  cpu: `${cpus()[0]?.model ?? "unknown"} x${cpus().length}`,
  memoryGb: (totalmem() / 1024 ** 3).toFixed(1),
  startedUtc: new Date().toISOString(),
};

const summaryPath = join(root, "reports", "load", `.load-001-${Date.now()}.tmp`);
mkdirSync(dirname(summaryPath), { recursive: true });

console.log(`LOAD-001: 20 equipment at 100 ms for ${seconds}s (${(seconds / 60).toFixed(1)} min)`);
if (environment.dirty) {
  console.log("WARNING: the working tree is dirty; the commit alone will not reproduce this run.");
}

let failure = null;
try {
  run("dotnet", [
    "test", "src/dotnet/EdgeGateway.Tests/Mair.EdgeGateway.Tests.csproj",
    "--nologo", "-v", "q",
    "--filter", "FullyQualifiedName~Load001_TwentyEquipmentAtTenHertz",
    "-e", "MAIR_LOAD_TEST=1",
    "-e", `MAIR_LOAD_SECONDS=${seconds}`,
    "-e", `MAIR_LOAD_OUTPUT=${summaryPath}`,
  ]);
} catch (error) {
  failure = error.stdout?.toString() ?? String(error);
}

const measured = {};
if (existsSync(summaryPath)) {
  for (const line of readFileSync(summaryPath, "utf8").split(/\r?\n/)) {
    const [key, value] = line.split("=");
    if (key && value !== undefined) measured[key] = value;
  }

  rmSync(summaryPath, { force: true });
}

const satisfiesAc = seconds >= REQUIRED_SECONDS && !failure;
const stamp = environment.startedUtc.replace(/[:.]/g, "-");
const reportPath = join(root, "reports", "load", `LOAD-001-${stamp}.md`);

const title = satisfiesAc
  ? "LOAD-001 — 20 equipment @ 100 ms for 30 min"
  : `LOAD-001 harness run (${(seconds / 60).toFixed(1)} min) — **DOES NOT SATISFY LOAD-001**`;

const lines = [
  `# ${title}`,
  "",
  satisfiesAc
    ? ""
    : `> This run lasted ${(seconds / 60).toFixed(1)} minutes. LOAD-001 requires **30**. It exercises` +
      "\n> the harness; it is not evidence for the criterion, and no performance claim may cite it.",
  "",
  "## Environment",
  "",
  "| | |",
  "|---|---|",
  ...Object.entries(environment).map(([k, v]) => `| ${k} | \`${v}\` |`),
  "",
  "## Measured",
  "",
  Object.keys(measured).length === 0
    ? "The run produced no summary — see the failure below."
    : ["| Metric | Value |", "|---|---|", ...Object.entries(measured).map(([k, v]) => `| ${k} | ${v} |`)].join("\n"),
  "",
  "## Result",
  "",
  failure ? `**FAILED**\n\n\`\`\`\n${failure.slice(-4000)}\n\`\`\`` : "**PASSED** — assertions in `LoadScenarioTests` held.",
  "",
  "## What this does and does not show",
  "",
  "The figures above are measurements of **this machine on this commit**. They are not the targets in",
  "`NON_FUNCTIONAL_REQUIREMENTS.md`, which stay `TARGET (unmeasured)` until a run on a representative",
  "deployment profile says otherwise. A developer laptop is not that profile.",
  "",
  "`sequence_gaps` is the assertion that carries weight: nothing induced loss during this run, so any",
  "non-zero value would be real loss, not a detector firing on a scenario.",
  "",
];

writeFileSync(reportPath, lines.filter((l) => l !== "").join("\n") + "\n", "utf8");

console.log(`\nReport: ${reportPath.replace(root, ".")}`);
for (const [k, v] of Object.entries(measured)) console.log(`  ${k} = ${v}`);

if (failure) {
  console.error("\nLOAD-001 failed. The report records the failure.");
  process.exit(1);
}

if (!satisfiesAc) {
  console.log(`\nNote: ${seconds}s run. LOAD-001 needs ${REQUIRED_SECONDS}s; this does not satisfy it.`);
}
