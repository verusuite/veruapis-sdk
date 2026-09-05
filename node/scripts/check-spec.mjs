// Compares the hand-written client against the API's own description.
//
// Hand-written types read far better than generated ones, and the price is
// drift. This is how that price is paid rather than ignored: it fails when the
// SDK claims a route the API does not serve, and warns when the API has grown
// an endpoint the SDK has not got to yet.
//
//   node scripts/check-spec.mjs
//
// Exits non-zero on a real disagreement, so CI catches it before a release
// does. A missing endpoint is a warning, because the SDK is allowed to lag; a
// route that does not exist is an error, because that is a method somebody
// will call and get a 404 from.

import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { parse } from "yaml";

const here = dirname(fileURLToPath(import.meta.url));
const specPath = join(here, "..", "..", "spec", "openapi.yaml");
const spec = parse(readFileSync(specPath, "utf8"));

// Every route the API describes, as "METHOD /path" with {id} normalised away,
// since the SDK interpolates values into the same shape.
const documented = new Set();
for (const [path, item] of Object.entries(spec.paths ?? {})) {
  for (const method of ["get", "post", "put", "patch", "delete"]) {
    if (!item[method]) continue;
    // A route the server does not implement yet is marked deprecated in the
    // description. The SDK should not offer it, so it does not count as
    // documented for this purpose.
    if (item[method].deprecated) continue;
    documented.add(`${method.toUpperCase()} ${normalise(path)}`);
  }
}

// Every route the SDK calls. Read from the source rather than by running it:
// the point is to see what the code says, not what one code path happens to do.
const source = ["resources.ts", "client.ts"]
  .map((f) => readFileSync(join(here, "..", "src", f), "utf8"))
  .join("\n");

const used = new Set();
const call = /this\.t\.(?:request|paginate)<?[^>]*>?\(\s*(?:"(GET|POST|PUT|PATCH|DELETE)",\s*)?[`"]([^`"]+)[`"]/g;

for (const m of source.matchAll(call)) {
  const method = m[1] ?? "GET"; // paginate is always a GET
  used.add(`${method} ${normalise(m[2])}`);
}

/** `/v1/messages/${encodeURIComponent(id)}` and `/v1/messages/{id}` are one route. */
function normalise(path) {
  return path
    .replace(/\$\{[^}]+\}/g, "{}")
    .replace(/\{[^}]+\}/g, "{}")
    .replace(/\/+$/, "");
}

const unknown = [...used].filter((r) => !documented.has(r)).sort();
const missing = [...documented].filter((r) => !used.has(r)).sort();

let failed = false;

if (unknown.length > 0) {
  failed = true;
  console.error("\nThe SDK calls routes the API does not serve:\n");
  for (const r of unknown) console.error(`  ${r}`);
  console.error("\nThese are 404s waiting to happen. Fix the SDK, or regenerate");
  console.error("the specification if the API really did change.\n");
}

if (missing.length > 0) {
  console.warn(`\n${missing.length} endpoint(s) the API serves and the SDK does not cover yet:\n`);
  for (const r of missing) console.warn(`  ${r}`);
  console.warn("\nNot an error: the SDK is allowed to lag, and request() reaches");
  console.warn("anything it has not wrapped. Worth adding when somebody needs one.\n");
}

if (!failed) {
  console.log(
    `spec check passed: ${used.size} route(s) used, all documented ` +
      `(${documented.size} live endpoints in the specification)`
  );
}

process.exit(failed ? 1 : 0);
