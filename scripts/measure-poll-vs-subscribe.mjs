#!/usr/bin/env node
// Measures the polled Modbus path against the section 1.3 OPC UA subscription, side by side:
// one equipment, both protocols, same duration, same machine.
//
// NFR-005 and AC-042 require every performance figure to be produced by a script and recorded with
// its environment, never typed by hand. This is that script. It captures the commit, machine and
// toolchain, runs the scenario, and writes a report under reports/load/.
//
//   node scripts/measure-poll-vs-subscribe.mjs [--seconds N]
//
// This decided OD-005, so it has to be reproducible: the report carries the commit, the machine and
// the duration, because a coverage percentage without them is an anecdote.

import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { cpus, totalmem, platform, release, arch } from "node:os";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const REQUIRED_SECONDS = 60; // a comparison, not an endurance run

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

console.log(`POLL-VS-SUBSCRIBE: 20 equipment at 100 ms for ${seconds}s (${(seconds / 60).toFixed(1)} min)`);
if (environment.dirty) {
  console.log("WARNING: the working tree is dirty; the commit alone will not reproduce this run.");
}

let failure = null;
try {
  run("dotnet", [
    "test", "src/dotnet/EdgeGateway.Tests/Mair.EdgeGateway.Tests.csproj",
    "--nologo", "-v", "q",
    "--filter", "FullyQualifiedName~PolledAndSubscribedPathsMeasuredSideBySide",
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
const reportPath = join(root, "reports", "load", `POLL-VS-SUBSCRIBE-${stamp}.md`);

const title = satisfiesAc
  ? "POLL-VS-SUBSCRIBE — 20 equipment @ 100 ms for 30 min"
  : `POLL-VS-SUBSCRIBE harness run (${(seconds / 60).toFixed(1)} min) — **DOES NOT SATISFY POLL-VS-SUBSCRIBE**`;

const lines = [
  `# ${title}`,
  "",
  satisfiesAc
    ? ""
    : `> This run lasted ${(seconds / 60).toFixed(1)} minutes. POLL-VS-SUBSCRIBE requires **30**. It exercises` +
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
  console.error("\nPOLL-VS-SUBSCRIBE failed. The report records the failure.");
  process.exit(1);
}

if (!satisfiesAc) {
  console.log(`\nNote: ${seconds}s run. POLL-VS-SUBSCRIBE needs ${REQUIRED_SECONDS}s; this does not satisfy it.`);
}
