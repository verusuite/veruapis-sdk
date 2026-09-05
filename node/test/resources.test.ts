import assert from "node:assert/strict";
import { describe, it } from "node:test";

import { CalendarApi, Mail } from "../src/resources.js";
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
      return undefined as T;
    },
    requestEnvelope: async <T>(method: Method, path: string, options: RequestOptions = {}) => {
      calls.push({ method, path, options });
      return { data: [] as unknown as T, meta: { request_id: "r", timestamp: "t" } } as Envelope<T>;
    },
    paginate: async function* <T>(path: string, options: RequestOptions = {}) {
      calls.push({ method: "GET", path, options });
      // Nothing to yield: the assertion is the call, not the rows.
    } as Transport["paginate"],
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
