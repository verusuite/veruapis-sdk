import assert from "node:assert/strict";
import { describe, it } from "node:test";

import { CalendarApi, Contacts, Documents, Files, Identity, Mail, Spreadsheets } from "../src/resources.js";
import type { Method, RequestOptions, Transport } from "../src/client.js";
import type { Envelope } from "../src/types.js";

/**
 * The grouped methods.
 *
 * These are a thin mapping from a name a developer thinks in to a route the API
 * serves, so what is worth testing is exactly that mapping: the method, the
 * path, and where each argument ended up. A recording transport makes each
 * assertion one line and keeps the network out of it.
 */

type Call = { method: Method; path: string; options: RequestOptions };

function recorder(): { calls: Call[]; transport: Transport } {
  const calls: Call[] = [];

  const transport: Transport = {
    request: async <T>(method: Method, path: string, options: RequestOptions = {}) => {
      calls.push({ method, path, options });
      // An object rather than undefined: a method that reads a field out of the
      // response — the batch read unwraps value_ranges — should be exercised
      // here rather than throwing on a shape no server sends.
      return {} as T;
    },
    requestEnvelope: async <T>(method: Method, path: string, options: RequestOptions = {}) => {
      calls.push({ method, path, options });
      return { data: [] as unknown as T, meta: { request_id: "r", timestamp: "t" } } as Envelope<T>;
    },
    paginate: async function* <T>(path: string, options: RequestOptions = {}) {
      calls.push({ method: "GET", path, options });
      // Nothing to yield: the assertion is the call, not the rows.
    } as Transport["paginate"],
    requestBytes: async (method: Method, path: string, options: RequestOptions = {}) => {
      calls.push({ method, path, options });
      return { bytes: new Uint8Array(), contentType: "application/pdf" };
    },
  };

  return { calls, transport };
}

describe("mail", () => {
  it("maps each method to its route", async () => {
    const { calls, transport } = recorder();
    const mail = new Mail(transport);

    await mail.listFolders();
    await mail.listMessages({ folder_id: "f1" });
    await mail.getMessage("m1");
    await mail.listAttachments("m1");
    await mail.listDrafts();
    await mail.send({ to: ["a@b.example"], subject: "x", text: "y" });

    assert.deepEqual(
      calls.map((c) => `${c.method} ${c.path}`),
      [
        "GET /v1/folders",
        "GET /v1/messages",
        "GET /v1/messages/m1",
        "GET /v1/messages/m1/attachments",
        "GET /v1/drafts",
        "POST /v1/messages/send",
      ]
    );
  });

  it("passes the filter as a query, not a body", async () => {
    const { calls, transport } = recorder();
    await new Mail(transport).listMessages({ folder_id: "f1", unread: true });

    assert.deepEqual(calls[0]!.options.query, { folder_id: "f1", unread: true });
    assert.equal(calls[0]!.options.body, undefined);
  });

  it("sends the message as the body", async () => {
    const { calls, transport } = recorder();
    const message = { to: ["a@b.example"], subject: "Hi", text: "There" };
    await new Mail(transport).send(message);

    assert.deepEqual(calls[0]!.options.body, message);
  });

  it("passes an idempotency key through when given one", async () => {
    const { calls, transport } = recorder();
    await new Mail(transport).send({ to: ["a@b.example"], subject: "x", text: "y" }, "key-1");

    assert.equal(calls[0]!.options.idempotencyKey, "key-1");
  });

  it("escapes an id that would otherwise change the route", async () => {
    const { calls, transport } = recorder();
    await new Mail(transport).getMessage("../admin");

    assert.equal(calls[0]!.path, "/v1/messages/..%2Fadmin");
  });

  it("pages messages through the cursor helper", async () => {
    const { calls, transport } = recorder();
    for await (const _ of new Mail(transport).messages({ folder_id: "f1" })) {
      // drain
    }

    assert.equal(calls[0]!.method, "GET");
    assert.equal(calls[0]!.path, "/v1/messages");
  });
});

describe("calendar", () => {
  it("maps each method to its route", async () => {
    const { calls, transport } = recorder();
    const cal = new CalendarApi(transport);

    await cal.listCalendars();
    await cal.getCalendar("c1");
    await cal.listEvents({ start: "s", end: "e" });
    await cal.getEvent("c1", "e1");
    await cal.createEvent("c1", { summary: "x", starts_at: "s", ends_at: "e" });
    await cal.updateEvent("c1", "e1", { summary: "y" });
    await cal.deleteEvent("c1", "e1");
    await cal.freeBusy(["a@b.example"], "s", "e");

    assert.deepEqual(
      calls.map((c) => `${c.method} ${c.path}`),
      [
        "GET /v1/calendars",
        "GET /v1/calendars/c1",
        "GET /v1/events",
        "GET /v1/calendars/c1/events/e1",
        "POST /v1/calendars/c1/events",
        "PATCH /v1/calendars/c1/events/e1",
        "DELETE /v1/calendars/c1/events/e1",
        "POST /v1/freebusy",
      ]
    );
  });

  it("requires the window on a listing, because a series has no finite end", async () => {
    const { calls, transport } = recorder();
    await new CalendarApi(transport).listEvents({ start: "2026-09-01", end: "2026-09-30" });

    assert.deepEqual(calls[0]!.options.query, { start: "2026-09-01", end: "2026-09-30" });
  });

  it("builds the free/busy body from its arguments", async () => {
    const { calls, transport } = recorder();
    await new CalendarApi(transport).freeBusy(["a@b.example", "c@d.example"], "s", "e");

    assert.deepEqual(calls[0]!.options.body, {
      emails: ["a@b.example", "c@d.example"],
      start: "s",
      end: "e",
    });
  });

  it("escapes both ids in a nested route", async () => {
    const { calls, transport } = recorder();
    await new CalendarApi(transport).getEvent("c/1", "e 1");

    assert.equal(calls[0]!.path, "/v1/calendars/c%2F1/events/e%201");
  });

  it("pages events through the cursor helper", async () => {
    const { calls, transport } = recorder();
    for await (const _ of new CalendarApi(transport).events({ start: "s", end: "e" })) {
      // drain
    }

    assert.equal(calls[0]!.path, "/v1/events");
  });

  it("passes an idempotency key on create", async () => {
    const { calls, transport } = recorder();
    await new CalendarApi(transport).createEvent(
      "c1",
      { summary: "x", starts_at: "s", ends_at: "e" },
      "key-1"
    );

    assert.equal(calls[0]!.options.idempotencyKey, "key-1");
  });
});

describe("spreadsheets", () => {
  it("maps each method to its route", async () => {
    const { calls, transport } = recorder();
    const sheets = new Spreadsheets(transport);

    await sheets.get("s1");
    await sheets.state("s1");
    await sheets.values("s1", "Sheet1!A1:B2");
    await sheets.batchValues("s1", ["Sheet1!A1"]);
    await sheets.write("s1", "Sheet1!A1", [["a"]]);
    await sheets.batchWrite("s1", [{ range: "Sheet1!A1", values: [["a"]] }]);
    await sheets.append("s1", "A:C", [["a"]]);
    await sheets.clear("s1", "A:C");
    await sheets.applyStructure("s1", "st", [{ add_sheet: { title: "Q4" } }]);

    assert.deepEqual(
      calls.map((c) => `${c.method} ${c.path}`),
      [
        "GET /v1/spreadsheets/s1",
        "GET /v1/spreadsheets/s1/state",
        "GET /v1/spreadsheets/s1/values/Sheet1!A1%3AB2",
        "POST /v1/spreadsheets/s1/values/batch-get",
        "PUT /v1/spreadsheets/s1/values/Sheet1!A1",
        "POST /v1/spreadsheets/s1/values/batch-update",
        "POST /v1/spreadsheets/s1/values/A%3AC/append",
        "POST /v1/spreadsheets/s1/values/A%3AC/clear",
        "POST /v1/spreadsheets/s1/batch-update",
      ]
    );
  });

  it("keeps a range in one segment, whatever it contains", async () => {
    const { calls, transport } = recorder();

    await new Spreadsheets(transport).values("s1", "'Q1 2026'!A1:D20");

    const range = calls[0].path.replace("/v1/spreadsheets/s1/values/", "");
    assert.ok(!range.includes("/"), `a range became more than one segment: ${range}`);
    assert.ok(range.includes("%20"), `the space was not escaped: ${range}`);
  });

  it("sends the state as the precondition on a structural change", async () => {
    const { calls, transport } = recorder();

    await new Spreadsheets(transport).applyStructure("s1", "a1b2c3", [
      { delete_sheet: { sheet_id: "sh1" } },
    ]);

    // Without it the API refuses the request outright, so a client that dropped
    // it would compile, call, and fail every time — and the failure would read
    // as a server problem rather than a missing header.
    assert.equal(calls[0].options.ifMatch, "a1b2c3");
  });

  it("asks for computed values only when told to", async () => {
    const { calls, transport } = recorder();
    const sheets = new Spreadsheets(transport);

    await sheets.values("s1", "A1");
    await sheets.values("s1", "A1", "stored");
    await sheets.values("s1", "A1", "computed");

    // Stored is the default and always works. Computed costs a pass over the
    // whole workbook, so it is never sent unless it was asked for.
    assert.equal(calls[0].options.query, undefined);
    assert.equal(calls[1].options.query, undefined);
    assert.deepEqual(calls[2].options.query, { value_render: "computed" });
  });

  it("unwraps the batch read rather than handing back the envelope's field", async () => {
    const calls: Call[] = [];
    const transport: Transport = {
      request: async <T>(method: Method, path: string, options: RequestOptions = {}) => {
        calls.push({ method, path, options });
        return { value_ranges: [{ range: "Sheet1!A1", values: [["a"]] }] } as T;
      },
      requestEnvelope: async <T>() => ({ data: [] as unknown as T, meta: { request_id: "r", timestamp: "t" } }),
      paginate: async function* () {} as Transport["paginate"],
    };

    const ranges = await new Spreadsheets(transport).batchValues("s1", ["Sheet1!A1"]);

    assert.equal(ranges.length, 1);
    assert.equal(ranges[0].range, "Sheet1!A1");
  });
});

describe("identity and contacts", () => {
  it("maps each method to its route", async () => {
    const { calls, transport } = recorder();

    await new Identity(transport).me();
    await new Identity(transport).listGroups({ limit: 25 });
    await new Contacts(transport).listAddressBooks();
    await new Contacts(transport).listContacts({ q: "bob" });
    await new Contacts(transport).getContact("k1");
    await new Contacts(transport).createContact({ name: "Bob" });
    await new Contacts(transport).updateContact("k1", { title: "Buyer" });
    await new Contacts(transport).deleteContact("k1");

    assert.deepEqual(
      calls.map((c) => `${c.method} ${c.path}`),
      [
        "GET /v1/me",
        "GET /v1/groups",
        "GET /v1/address-books",
        "GET /v1/contacts",
        "GET /v1/contacts/k1",
        "POST /v1/contacts",
        "PATCH /v1/contacts/k1",
        "DELETE /v1/contacts/k1",
      ]
    );
  });

  it("passes the search term as q, which matches secondary addresses too", async () => {
    const { calls, transport } = recorder();

    await new Contacts(transport).listContacts({ q: "bob", address_book_id: "abk1" });

    assert.deepEqual(calls[0].options.query, { q: "bob", address_book_id: "abk1" });
  });
});

describe("documents", () => {
  it("maps each method to its route", async () => {
    const { calls, transport } = recorder();
    const docs = new Documents(transport);

    await docs.listDocuments({ type: "spreadsheet" });
    await docs.getDocument("d1");
    await docs.createDocument({ type: "spreadsheet", title: "Q3" });
    await docs.updateDocument("d1", { title: "Q4" });
    await docs.deleteDocument("d1");
    await docs.restoreDocument("d1");
    await docs.copyDocument("d1");
    await docs.listComments("d1");
    await docs.createComment("d1", { body: "x" });
    await docs.updateComment("n1", { state: "resolved" });
    await docs.deleteComment("n1");

    assert.deepEqual(
      calls.map((c) => `${c.method} ${c.path}`),
      [
        "GET /v1/documents",
        "GET /v1/documents/d1",
        "POST /v1/documents",
        "PATCH /v1/documents/d1",
        "DELETE /v1/documents/d1",
        "POST /v1/documents/d1/restore",
        "POST /v1/documents/d1/copy",
        "GET /v1/documents/d1/comments",
        "POST /v1/documents/d1/comments",
        "PATCH /v1/comments/n1",
        "DELETE /v1/comments/n1",
      ]
    );
  });

  it("asks for the whole comment history unless told otherwise", async () => {
    const { calls, transport } = recorder();
    const docs = new Documents(transport);

    await docs.listComments("d1");
    await docs.listComments("d1", false);

    // The default is the opposite of the editor's sidebar: an integration
    // auditing a document wants the resolved threads too.
    assert.equal(calls[0].options.query, undefined);
    assert.deepEqual(calls[1].options.query, { include_resolved: "false" });
  });
});

describe("files", () => {
  it("maps each method to its route", async () => {
    const { calls, transport } = recorder();
    const files = new Files(transport);

    await files.listFolders();
    await files.createFolder({ name: "Reports" });
    await files.updateFolder("o1", { name: "Archive" });
    await files.deleteFolder("o1");
    await files.listFiles({ folder_id: "o1" });
    await files.getFile("b1");
    await files.download("b1");
    await files.updateFile("b1", { name: "final.pdf" });
    await files.deleteFile("b1");
    await files.startUpload({ filename: "f.pdf", size: 10 });
    await files.uploadPart("u1", 1, new Uint8Array([1, 2, 3]));
    await files.uploadStatus("u1");
    await files.completeUpload("u1");
    await files.abortUpload("u1");
    await files.listPermissions("b1");
    await files.share("b1", { principal_id: "usr1", role: "editor" });
    await files.unshare("b1", "p1");

    assert.deepEqual(
      calls.map((c) => `${c.method} ${c.path}`),
      [
        "GET /v1/file-folders",
        "POST /v1/file-folders",
        "PATCH /v1/file-folders/o1",
        "DELETE /v1/file-folders/o1",
        "GET /v1/files",
        "GET /v1/files/b1",
        "GET /v1/files/b1/content",
        "PATCH /v1/files/b1",
        "DELETE /v1/files/b1",
        "POST /v1/files/uploads",
        "PUT /v1/files/uploads/u1/parts/1",
        "GET /v1/files/uploads/u1",
        "POST /v1/files/uploads/u1/complete",
        "DELETE /v1/files/uploads/u1",
        "GET /v1/files/b1/permissions",
        "POST /v1/files/b1/permissions",
        "DELETE /v1/files/b1/permissions/p1",
      ]
    );
  });

  it("sends a part as bytes rather than as JSON", async () => {
    const { calls, transport } = recorder();

    await new Files(transport).uploadPart("u1", 2, new Uint8Array([1, 2, 3]));

    // JSON-encoding a file would inflate it and corrupt anything that is not
    // valid UTF-8, which is most of what people upload.
    assert.equal(calls[0].options.body, undefined);
    assert.deepEqual(calls[0].options.rawBody, new Uint8Array([1, 2, 3]));
    assert.equal(calls[0].options.contentType, "application/octet-stream");
  });

  it("forwards a range so a large file can be fetched in parts", async () => {
    const { calls, transport } = recorder();

    await new Files(transport).download("b1", "bytes=0-1048575");

    assert.equal(calls[0].options.range, "bytes=0-1048575");
  });
});
