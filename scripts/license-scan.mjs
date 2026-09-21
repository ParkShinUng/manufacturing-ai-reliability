#!/usr/bin/env node
// Licence inventory for the full transitive NuGet graph.
//
// TOOLCHAIN.md's verification procedure requires this before the first commit of a phase that adds
// dependencies, and ADR-0020 deliberately narrowed its licence claim to the DIRECT packages because
// the graph had not been scanned. This closes that gap.
//
// Why it matters here specifically: this is a public portfolio repository, and the OPC UA stack has
// a wide dependency graph. A single copyleft or membership-gated package anywhere in it would be
// disqualifying, and "the direct packages are MIT" says nothing about the other eighty.
//
// Reads licence metadata from the .nuspec already on disk in the NuGet global cache, so it needs no
// network and no extra tooling. Usage:
//
//   node scripts/license-scan.mjs [--json]

import { execFileSync } from "node:child_process";
import { existsSync, readFileSync, readdirSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

const SOLUTION = "src/dotnet/Mair.sln";

// Permissive licences that impose no obligation beyond attribution. Anything not on this list is
// reported for a human to look at rather than silently accepted - an unknown licence is not a
// passing licence.
const PERMISSIVE = new Set([
  "MIT", "MIT-0", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "ISC", "0BSD", "Unlicense",
  "MS-PL", "MICROSOFT SOFTWARE LICENSE TERMS", "MICROSOFT .NET LIBRARY",
]);

const packagesRoot = join(homedir(), ".nuget", "packages");

// Signatures taken from the canonical texts. Matching on the operative wording rather than the
// title, because a retitled or vendor-prefixed header is common and the obligations live in the
// body - the OPC Foundation's file, for instance, is headed with its own name and is MIT beneath.
const TEXT_SIGNATURES = [
  [/Permission is hereby granted, free of charge, to any person obtaining a copy/i, "MIT"],
  [/Apache License\s*,?\s*Version 2\.0/i, "Apache-2.0"],
  [/Redistribution and use in source and binary forms[\s\S]{0,400}3\. Neither the name/i, "BSD-3-Clause"],
  [/Redistribution and use in source and binary forms/i, "BSD-2-Clause"],
  [/Permission to use, copy, modify, and\/?or distribute this software for any purpose/i, "ISC"],
  [/GNU GENERAL PUBLIC LICENSE/i, "GPL"],
  [/GNU LESSER GENERAL PUBLIC LICENSE/i, "LGPL"],
  [/Mozilla Public License/i, "MPL-2.0"],
  [/Reciprocal Community License/i, "RCL"],
];

function identifyLicenceText(id, version, relativePath) {
  const dir = join(packagesRoot, id.toLowerCase(), version.toLowerCase());
  const candidate = join(dir, relativePath);
  if (!existsSync(candidate)) return null;

  // Whitespace is collapsed before matching. Licence files are hard-wrapped, so a signature that
  // expects a contiguous sentence matches almost nothing - the first version of this script
  // reported all six OPC Foundation packages as UNIDENTIFIED for exactly that reason, when their
  // packaged LICENSE.txt is plainly MIT.
  const text = readFileSync(candidate, "utf8").replace(/\s+/g, " ");

  for (const [pattern, licence] of TEXT_SIGNATURES) {
    if (pattern.test(text)) {
      return { licence, detail: `identified from ${relativePath}` };
    }
  }

  return { licence: "UNIDENTIFIED", detail: `${relativePath} matched no known licence text` };
}

function listPackages() {
  const output = execFileSync(
    "dotnet",
    ["list", SOLUTION, "package", "--include-transitive"],
    { encoding: "utf8", maxBuffer: 32 * 1024 * 1024 },
  );

  const found = new Map();
  for (const line of output.split(/\r?\n/)) {
    // Both "   > Package  requested  resolved" and the transitive "   > Package  resolved" shapes.
    const m = line.match(/^\s*>\s+(\S+)\s+(?:\S+\s+)?(\d[\w.+-]*)\s*$/);
    if (m) found.set(`${m[1]}|${m[2]}`, { id: m[1], version: m[2] });
  }

  return [...found.values()].sort((a, b) => a.id.localeCompare(b.id));
}

function nuspecOf(id, version) {
  // The cache lower-cases directory names.
  const dir = join(packagesRoot, id.toLowerCase(), version.toLowerCase());
  if (!existsSync(dir)) return null;

  const file = readdirSync(dir).find((f) => f.toLowerCase().endsWith(".nuspec"));
  return file ? readFileSync(join(dir, file), "utf8") : null;
}

function licenceOf(id, version) {
  const nuspec = nuspecOf(id, version);
  if (!nuspec) return { licence: "UNKNOWN", detail: "package not in the local NuGet cache" };

  const expression = nuspec.match(/<license\s+type="expression"[^>]*>([^<]+)<\/license>/i);
  if (expression) return { licence: expression[1].trim(), detail: "SPDX expression" };

  const file = nuspec.match(/<license\s+type="file"[^>]*>([^<]+)<\/license>/i);
  if (file) {
    // A packaged licence file is the common case for the OPC UA stack and NModbus, and leaving it
    // as "unknown" would have handed a reader nine unanswered questions. Read it and identify it.
    const identified = identifyLicenceText(id, version, file[1].trim());
    return identified ?? { licence: "FILE", detail: `unreadable licence file: ${file[1].trim()}` };
  }

  const url = nuspec.match(/<licenseUrl>([^<]+)<\/licenseUrl>/i);
  if (url) return { licence: "URL", detail: url[1].trim() };

  return { licence: "UNKNOWN", detail: "no licence metadata in the .nuspec" };
}

const packages = listPackages();
if (packages.length === 0) {
  console.error("No packages found. Has `dotnet restore` run?");
  process.exit(1);
}

const rows = packages.map((p) => ({ ...p, ...licenceOf(p.id, p.version) }));

// An SPDX expression can combine terms; every term has to be permissive for the whole to be.
const isPermissive = (licence) =>
  licence.split(/\s+(?:OR|AND)\s+/i).every((term) => PERMISSIVE.has(term.trim().toUpperCase())
    || PERMISSIVE.has(term.trim()));

const review = rows.filter((r) => !isPermissive(r.licence));

if (process.argv.includes("--json")) {
  console.log(JSON.stringify({ scanned: rows.length, rows }, null, 2));
} else {
  const width = Math.max(...rows.map((r) => r.id.length));
  for (const r of rows) {
    const mark = isPermissive(r.licence) ? " " : "!";
    console.log(`${mark} ${r.id.padEnd(width)}  ${r.version.padEnd(16)}  ${r.licence}`);
  }
}

console.log(`\n${rows.length} packages scanned, ${review.length} needing review.`);

if (review.length === 0) {
  console.log("Every package carries a permissive SPDX licence.");
  process.exit(0);
}

console.log("\nNeeding review — a licence this script cannot classify is not a failure by itself,");
console.log("but it is not a pass either, and someone has to look at it:");
for (const r of review) {
  console.log(`  ${r.id} ${r.version}\n    ${r.licence}: ${r.detail}`);
}

// Deliberately exit 0: this is an inventory for a human, not a gate. Making it a gate would need an
// agreed policy on FILE and URL licences first, and inventing that policy here would be exactly the
// kind of undocumented decision AGENTS.md forbids.
process.exit(0);
