// Compares the hand-written client against the API's own description.
//
// Hand-written types read far better than generated ones, and the price is
// drift. This is how that price is paid rather than ignored: it fails when the
// SDK claims a route the API does not serve, and warns when the API has grown
// an endpoint the SDK has not got to yet.
//
//   npm run build && node scripts/check-spec.mjs
//
// Exits non-zero on a real disagreement, so CI catches it before a release
// does. A missing endpoint is a warning, because the SDK is allowed to lag; a
// route that does not exist is an error, because that is a method somebody
// will call and get a 404 from.
//
// The routes are collected by CALLING every method against a recording server,
// which is how the Go, Python and .NET checks have always worked and how this
// one now does. Reading the source was the first approach in both languages and
// it is wrong in a way that hides: a path built by interpolation or through a
// helper gives up only its first literal to a regular expression, so a real
// route is reported as unwrapped and a truncated prefix can match one nobody
// wrote. Running the client is exact, and it exercises every method as a side
// effect — a typo in a path stops being something a reader has to notice.
//
// It runs against dist/, so the build has to be current. That is the one cost
// of this form, and prepublishOnly already builds.

import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { createServer } from "node:http";
import { parse } from "yaml";

import { VeruApi } from "../dist/client.js";

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

// The values every call below passes, so a concrete path can be turned back
// into the templated one the description uses. A range is one of them: it is a
// path segment the caller supplies, and "r1" stands in for A1 notation.
const ids = new Set(["f1", "m1", "c1", "e1", "s1", "r1", "k1", "d1", "n1", "o1", "b1", "u1", "p1", "1"]);

const used = new Set();

const server = createServer((req, res) => {
  const url = new URL(req.url, "http://localhost");
  used.add(`${req.method} ${templated(url.pathname)}`);
  res.setHeader("Content-Type", "application/json");
  // An empty list satisfies most return shapes, and an absent cursor stops the
  // iterators after one page.
  res.end(JSON.stringify({ data: [], meta: { request_id: "r", timestamp: "t" } }));
});

await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
const baseUrl = `http://127.0.0.1:${server.address().port}`;

const api = new VeruApi({ apiKey: "vak_live_test_secret", baseUrl, maxRetries: 0 });

// One invocation of every method the client offers.
//
// Adding a method here is the price of adding one to the client, and it is the
// right price: an unlisted method is one nothing has ever called, which is how
// a typo in a path ships.
const everyCall = [
  () => api.mail.listFolders(),
  () => api.mail.listMessages({ folder_id: ["f1"] }),
  () => api.mail.getMessage("m1"),
  () => api.mail.send({ to: ["a@b.example"], subject: "x", text: "y" }),

  () => api.calendar.listCalendars(),
  () => api.calendar.getCalendar("c1"),
  () => api.calendar.listEvents({ start: "s", end: "e" }),
  () => api.calendar.getEvent("c1", "e1"),
  () => api.calendar.createEvent("c1", { summary: "x" }),
  () => api.calendar.updateEvent("c1", "e1", { summary: "y" }),
  () => api.calendar.deleteEvent("c1", "e1"),
  () => api.calendar.freeBusy(["a@b.example"], "s", "e"),

  () => api.spreadsheets.get("s1"),
  () => api.spreadsheets.state("s1"),
  () => api.spreadsheets.values("s1", "r1", "computed"),
  () => api.spreadsheets.batchValues("s1", ["Sheet1!A1:B2"]),
  () => api.spreadsheets.write("s1", "r1", [["a"]]),
  () => api.spreadsheets.batchWrite("s1", [{ range: "Sheet1!A1", values: [["a"]] }]),
  () => api.spreadsheets.append("s1", "r1", [["a"]]),
  () => api.spreadsheets.clear("s1", "r1"),
  () => api.spreadsheets.applyStructure("s1", "state-token", [{ add_sheet: { title: "Q4" } }]),

  () => api.identity.me(),
  () => api.identity.listGroups({ limit: 25 }),

  () => api.contacts.listAddressBooks(),
  () => api.contacts.listContacts({ limit: 25 }),
  () => api.contacts.getContact("k1"),
  () => api.contacts.createContact({ name: "Bob" }),
  () => api.contacts.updateContact("k1", { title: "Buyer" }),
  () => api.contacts.deleteContact("k1"),

  () => api.documents.listDocuments({ limit: 25 }),
  () => api.documents.getDocument("d1"),
  () => api.documents.createDocument({ type: "spreadsheet" }),
  () => api.documents.updateDocument("d1", { title: "x" }),
  () => api.documents.deleteDocument("d1"),
  () => api.documents.restoreDocument("d1"),
  () => api.documents.copyDocument("d1"),
  () => api.documents.listComments("d1"),
  () => api.documents.createComment("d1", { body: "x" }),
  () => api.documents.updateComment("n1", { state: "resolved" }),
  () => api.documents.deleteComment("n1"),

  () => api.files.listFolders(),
  () => api.files.createFolder({ name: "Reports" }),
  () => api.files.updateFolder("o1", { name: "Archive" }),
  () => api.files.deleteFolder("o1"),
  () => api.files.listFiles({ limit: 25 }),
  () => api.files.getFile("b1"),
  () => api.files.download("b1"),
  () => api.files.updateFile("b1", { name: "f.pdf" }),
  () => api.files.deleteFile("b1"),
  () => api.files.startUpload({ filename: "f.pdf", size: 10 }),
  () => api.files.uploadPart("u1", 1, new Uint8Array([1])),
  () => api.files.uploadStatus("u1"),
  () => api.files.completeUpload("u1"),
  () => api.files.abortUpload("u1"),
  () => api.files.listPermissions("b1"),
  () => api.files.share("b1", { principal_id: "usr1", role: "editor" }),
  () => api.files.unshare("b1", "p1"),

  // The iterators, drained so their first request is made.
  async () => {
    for await (const _ of api.mail.messages({ folder_id: ["f1"] })) break;
  },
  async () => {
    for await (const _ of api.calendar.events({ start: "s", end: "e" })) break;
  },
  async () => {
    for await (const _ of api.identity.groups()) break;
  },
  async () => {
    for await (const _ of api.contacts.contacts()) break;
  },
  async () => {
    for await (const _ of api.documents.documents()) break;
  },
  async () => {
    for await (const _ of api.files.files()) break;
  },
];

for (const call of everyCall) {
  // A method whose return shape the canned envelope does not satisfy still made
  // its request, and the request is what is being recorded.
  await call().catch(() => {});
}

server.close();

if (used.size === 0) {
  console.error("no routes were recorded; the harness is broken, not the client");
  process.exit(1);
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

/** A concrete path back to the templated one: /v1/messages/m1 is /v1/messages/{}. */
function templated(path) {
  return path
    .split("/")
    .map((segment) => (ids.has(safeDecode(segment)) ? "{}" : segment))
    .join("/")
    .replace(/\/+$/, "");
}

function safeDecode(segment) {
  try {
    return decodeURIComponent(segment);
  } catch {
    return segment;
  }
}

function normalise(path) {
  return path.replace(/\{[^}]+\}/g, "{}").replace(/\/+$/, "");
}
