#!/usr/bin/env node
// Mints an ES256 bearer token for the Operations API, for local and demo runs only (OD-017,
// ADR-0024). No package: Node's built-in crypto.
//
//   node scripts/mint-token.mjs --sub alice --roles viewer[,operator] [--lifetime 900]
//                               [--keys-dir .local/keys] [--issuer mair-local-issuer]
//                               [--audience mair-operations-api]
//
// On first use it generates a P-256 key pair into the keys directory - git-ignored by name - as
// operations-api-es256.private.jwk and operations-api-es256.public.jwk. The API is given the
// public file only. kid is the key's RFC 7638 thumbprint. The token goes to stdout.

import { createHash, createPrivateKey, generateKeyPairSync, sign } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const MAX_LIFETIME = 3600;

const args = Object.fromEntries(
  process.argv.slice(2).reduce((pairs, arg, i, all) => {
    if (arg.startsWith("--")) pairs.push([arg.slice(2), all[i + 1]]);
    return pairs;
  }, []),
);

const fail = (message) => {
  console.error(`mint-token: ${message}`);
  process.exit(1);
};

const sub = args.sub ?? fail("--sub is required");
const roles = (args.roles ?? fail("--roles is required")).split(",").filter(Boolean);
const lifetime = Number(args.lifetime ?? 900);
if (!Number.isInteger(lifetime) || lifetime <= 0 || lifetime > MAX_LIFETIME) {
  fail(`--lifetime must be 1..${MAX_LIFETIME} seconds; the API refuses longer tokens`);
}

const keysDir = args["keys-dir"] ?? ".local/keys";
const privatePath = join(keysDir, "operations-api-es256.private.jwk");
const publicPath = join(keysDir, "operations-api-es256.public.jwk");

const base64url = (buffer) => Buffer.from(buffer).toString("base64url");

// RFC 7638: the required members, lexicographic order, no whitespace.
const thumbprint = ({ crv, kty, x, y }) =>
  base64url(createHash("sha256").update(JSON.stringify({ crv, kty, x, y })).digest());

if (!existsSync(privatePath)) {
  mkdirSync(keysDir, { recursive: true });
  const { privateKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
  const jwk = privateKey.export({ format: "jwk" });
  const kid = thumbprint(jwk);
  writeFileSync(privatePath, JSON.stringify({ ...jwk, kid, alg: "ES256", use: "sig" }), { mode: 0o600 });
  const { d: _private, ...publicJwk } = jwk;
  writeFileSync(publicPath, JSON.stringify({ ...publicJwk, kid, alg: "ES256", use: "sig" }));
}

const privateJwk = JSON.parse(readFileSync(privatePath, "utf8"));
const key = createPrivateKey({ key: privateJwk, format: "jwk" });

const now = Math.floor(Date.now() / 1000);
const header = { alg: "ES256", typ: "JWT", kid: privateJwk.kid };
const payload = {
  iss: args.issuer ?? "mair-local-issuer",
  aud: args.audience ?? "mair-operations-api",
  sub,
  roles,
  iat: now,
  exp: now + lifetime,
};

const signingInput = `${base64url(JSON.stringify(header))}.${base64url(JSON.stringify(payload))}`;
// JWS ES256 is r||s (IEEE P1363), not the DER that Node produces by default.
const signature = sign("sha256", Buffer.from(signingInput), { key, dsaEncoding: "ieee-p1363" });
process.stdout.write(`${signingInput}.${base64url(signature)}\n`);
