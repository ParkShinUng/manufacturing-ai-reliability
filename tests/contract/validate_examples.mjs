// Contract test: every documented example must be a valid instance of its schema.
//
// This exists because in v0.2 the telemetry example in docs/03-contracts/EVENT_CONTRACTS.md
// omitted three schema-required measurements (voltageV, torqueNm, operationRatePct) and would
// have failed validation against contracts/jsonschema/v1/telemetry.schema.json (GAP-001).
// A producer implemented from the documented example would have emitted rejected events.
//
// Dependency-free on purpose: this must be runnable in the specification phase, before any
// package.json or node_modules exists. It implements the subset of JSON Schema the contracts use:
// type (incl. union), const, enum, required, properties, additionalProperties, items,
// uniqueItems, minItems, minimum, maximum, minLength, maxLength, pattern, allOf/if/then.
//
//   node tests/contract/validate_examples.mjs
// Exit 0 = all examples valid. Exit 1 = at least one violation.

import { readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";

const SCHEMA_DIR = "contracts/jsonschema/v1";
const EX_DIR = "contracts/examples";
const manifest = JSON.parse(readFileSync(join(EX_DIR, "_manifest.json"), "utf8"));

const errors = [];
const push = (p, m) => errors.push(`${p || "<root>"}: ${m}`);

function typeOk(v, t) {
  switch (t) {
    case "object": return v !== null && typeof v === "object" && !Array.isArray(v);
    case "array": return Array.isArray(v);
    case "string": return typeof v === "string";
    case "number": return typeof v === "number";
    case "integer": return typeof v === "number" && Number.isInteger(v);
    case "boolean": return typeof v === "boolean";
    case "null": return v === null;
    default: return true;
  }
}

function validate(value, schema, path) {
  if (schema === true || schema === undefined) return;

  if ("const" in schema && value !== schema.const) {
    push(path, `expected const ${JSON.stringify(schema.const)}, got ${JSON.stringify(value)}`);
  }

  if (schema.not) {
    const probe = errors.length;
    validate(value, schema.not, path);
    const matched = errors.length === probe;
    errors.length = probe;
    if (matched) push(path, `value ${JSON.stringify(value)} is forbidden by "not"`);
  }

  if (schema.enum) {
    // enum entries may include null
    if (!schema.enum.some((e) => e === value)) {
      push(path, `value ${JSON.stringify(value)} not in enum`);
    }
  }

  if (schema.type) {
    const types = Array.isArray(schema.type) ? schema.type : [schema.type];
    if (!types.some((t) => typeOk(value, t))) {
      push(path, `expected type ${types.join("|")}, got ${value === null ? "null" : Array.isArray(value) ? "array" : typeof value}`);
      return; // further checks are meaningless on the wrong type
    }
  }

  if (value === null) return;

  if (typeof value === "number") {
    if (schema.minimum !== undefined && value < schema.minimum) push(path, `${value} < minimum ${schema.minimum}`);
    if (schema.maximum !== undefined && value > schema.maximum) push(path, `${value} > maximum ${schema.maximum}`);
  }

  if (typeof value === "string") {
    if (schema.minLength !== undefined && value.length < schema.minLength) push(path, `shorter than minLength ${schema.minLength}`);
    if (schema.maxLength !== undefined && value.length > schema.maxLength) push(path, `longer than maxLength ${schema.maxLength}`);
    if (schema.pattern && !new RegExp(schema.pattern).test(value)) push(path, `"${value}" does not match /${schema.pattern}/`);
  }

  if (Array.isArray(value)) {
    if (schema.minItems !== undefined && value.length < schema.minItems) push(path, `fewer than minItems ${schema.minItems}`);
    if (schema.uniqueItems) {
      const seen = new Set(value.map((v) => JSON.stringify(v)));
      if (seen.size !== value.length) push(path, "items are not unique");
    }
    if (schema.maxItems !== undefined && value.length > schema.maxItems) push(path, `more than maxItems ${schema.maxItems}`);
    // prefixItems pins positional order (used to fix the canonical 13-gate list in order).
    if (schema.prefixItems) {
      schema.prefixItems.forEach((ps, i) => {
        if (i < value.length) validate(value[i], ps, `${path}[${i}]`);
        else push(path, `missing required positional item ${i}`);
      });
    }
    if (schema.items) value.forEach((v, i) => validate(v, schema.items, `${path}[${i}]`));
  }

  if (typeOk(value, "object")) {
    for (const r of schema.required || []) {
      if (!(r in value)) push(path, `missing required property "${r}"`);
    }
    const props = schema.properties || {};
    if (schema.additionalProperties === false) {
      for (const k of Object.keys(value)) {
        if (!(k in props)) push(path, `additional property "${k}" not allowed`);
      }
    }
    for (const [k, v] of Object.entries(value)) {
      if (props[k]) validate(v, props[k], path ? `${path}.${k}` : k);
    }
    for (const sub of schema.allOf || []) {
      if (sub.if && sub.then) {
        let matches = true;
        const probe = errors.length;
        validate(value, sub.if, path);
        matches = errors.length === probe;
        errors.length = probe; // the `if` branch must not itself report
        if (matches) validate(value, sub.then, path);
      } else {
        validate(value, sub, path);
      }
    }
  }
}

let failed = 0;
const files = readdirSync(EX_DIR).filter((f) => f.endsWith(".json") && f !== "_manifest.json");

for (const f of files) {
  const schemaName = manifest[f];
  if (!schemaName) {
    console.error(`FAIL ${f}: no schema mapped in _manifest.json`);
    failed++;
    continue;
  }
  const schema = JSON.parse(readFileSync(join(SCHEMA_DIR, schemaName), "utf8"));
  const instance = JSON.parse(readFileSync(join(EX_DIR, f), "utf8"));
  errors.length = 0;
  validate(instance, schema, "");
  if (errors.length) {
    failed++;
    console.error(`FAIL ${f}  (against ${schemaName})`);
    for (const e of errors) console.error(`    - ${e}`);
  } else {
    console.log(`ok   ${f}  (against ${schemaName})`);
  }
}

// Every schema must be exercised by at least one example, or a contract can drift unnoticed.
const covered = new Set(Object.values(manifest));
const allSchemas = readdirSync(SCHEMA_DIR).filter((f) => f.endsWith(".json") && f !== "envelope.schema.json");
const uncovered = allSchemas.filter((s) => !covered.has(s));
if (uncovered.length) {
  console.error(`\nFAIL coverage: no example for ${uncovered.join(", ")}`);
  failed++;
}

console.log(`\n${files.length} examples checked, ${failed} failing.`);
process.exit(failed ? 1 : 0);
