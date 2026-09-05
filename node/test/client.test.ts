import assert from "node:assert/strict";
import { describe, it } from "node:test";

import { VeruApi, VeruApiError } from "../src/client.js";
import type { Envelope } from "../src/types.js";

/**
 * The transport.
 *
 * Every test drives a fake fetch rather than the network, so what is asserted
 * is the exact request this client builds: the header it sends, the shape of a
 * repeated query parameter, whether a failure was retried. A test against a
 * live API would assert the API instead, and would pass for the wrong reason
 * the day the API changed.
 */

type Handler = (req: { url: URL; init: RequestInit; attempt: number }) => Response | Promise<Response>;

/** Builds a client over a scripted fetch, and records what it was asked. */
function client(handler: Handler, options: Partial<ConstructorParameters<typeof VeruApi>[0]> = {}) {
  const seen: { url: URL; init: RequestInit }[] = [];

  const api = new VeruApi({
    apiKey: "vak_live_abc_secret",
    baseUrl: "https://api.test",
    maxRetries: 0,
    ...options,
    fetch: (input: any, init: any = {}) => {
      const url = new URL(String(input));
      seen.push({ url, init });
      return Promise.resolve(handler({ url, init, attempt: seen.length - 1 }));
    },
  });

  return { api, seen };
}

function ok<T>(data: T, meta: Partial<Envelope<T>["meta"]> = {}): Response {
  return Response.json({
    data,
    meta: { request_id: "req_1", timestamp: "2026-09-05T00:00:00Z", ...meta },
  });
}

function fail(status: number, code: string, headers: Record<string, string> = {}): Response {
  return Response.json(
    { error: { code, message: "Refused.", request_id: "req_err" } },
    { status, headers }
  );
}

describe("construction", () => {
  it("refuses to build without a key, rather than failing on the first call", () => {
    assert.throws(() => new VeruApi({ apiKey: "" }), /apiKey is required/);
    assert.throws(() => new VeruApi(undefined as any), /apiKey is required/);
  });

  it("defaults to production and trims a trailing slash off a custom base", async () => {
    const { api, seen } = client(() => ok([]), { baseUrl: "https://api.test/" });
    await api.mail.listFolders();
    assert.equal(seen[0]!.url.toString(), "https://api.test/v1/folders");
  });

  it("exposes the grouped resources", () => {
    const { api } = client(() => ok([]));
    assert.ok(api.mail);
    assert.ok(api.calendar);
  });
});

describe("requests", () => {
  it("sends the key as a bearer token", async () => {
    const { api, seen } = client(() => ok([]));
    await api.mail.listFolders();

    const headers = seen[0]!.init.headers as Record<string, string>;
    assert.equal(headers.Authorization, "Bearer vak_live_abc_secret");
    assert.equal(headers.Accept, "application/json");
  });

  it("unwraps the envelope", async () => {
    const { api } = client(() => ok([{ id: "f1", name: "INBOX" }]));
    const folders = await api.mail.listFolders();
    assert.equal(folders[0]!.name, "INBOX");
  });

  it("returns the envelope when asked, so a caller can read the request id", async () => {
    const { api } = client(() => ok([], { request_id: "req_xyz" }));
    const page = await api.requestEnvelope("GET", "/v1/folders");
    assert.equal(page.meta.request_id, "req_xyz");
  });

  it("repeats a list parameter rather than joining it", async () => {
    // Comma-joining would be read by the API as one folder id, which is the
    // kind of bug that returns an empty list rather than an error.
    const { api, seen } = client(() => ok([]));
    await api.mail.listMessages({ folder_id: ["a", "b"] });

    assert.deepEqual(seen[0]!.url.searchParams.getAll("folder_id"), ["a", "b"]);
  });

  it("leaves an undefined query parameter out entirely", async () => {
    const { api, seen } = client(() => ok([]));
    await api.mail.listMessages({ folder_id: "a", cursor: undefined });

    assert.equal(seen[0]!.url.searchParams.has("cursor"), false);
  });

  it("escapes an id into the path", async () => {
    const { api, seen } = client(() => ok({}));
    await api.mail.getMessage("a/b?c");

    assert.equal(seen[0]!.url.pathname, "/v1/messages/a%2Fb%3Fc");
  });

  it("sends a JSON body with the right content type", async () => {
    const { api, seen } = client(() => ok({ id: "m1" }));
    await api.mail.send({ to: ["a@b.example"], subject: "Hi", text: "There" });

    const headers = seen[0]!.init.headers as Record<string, string>;
    assert.equal(headers["Content-Type"], "application/json");
    assert.deepEqual(JSON.parse(seen[0]!.init.body as string).to, ["a@b.example"]);
  });

  it("handles 204 without trying to parse a body", async () => {
    const { api } = client(() => new Response(null, { status: 204 }));
    const result = await api.calendar.deleteEvent("c1", "e1");
    assert.equal(result, undefined);
  });
});

describe("idempotency", () => {
  it("generates a key for a write, so a retry cannot send twice", async () => {
    const { api, seen } = client(() => ok({ id: "m1" }));
    await api.mail.send({ to: ["a@b.example"], subject: "x", text: "y" });

    const headers = seen[0]!.init.headers as Record<string, string>;
    assert.match(headers["Idempotency-Key"]!, /^[0-9a-f-]{36}$/);
  });

  it("uses the caller's key when there is one", async () => {
    const { api, seen } = client(() => ok({ id: "m1" }));
    await api.mail.send({ to: ["a@b.example"], subject: "x", text: "y" }, "my-key");

    const headers = seen[0]!.init.headers as Record<string, string>;
    assert.equal(headers["Idempotency-Key"], "my-key");
  });

  it("sends none on a read, which has nothing to make idempotent", async () => {
    const { api, seen } = client(() => ok([]));
    await api.mail.listFolders();

    const headers = seen[0]!.init.headers as Record<string, string>;
    assert.equal(headers["Idempotency-Key"], undefined);
  });
});

describe("errors", () => {
  it("carries the code, status and request id", async () => {
    const { api } = client(() => fail(403, "insufficient_scope"));

    await assert.rejects(api.mail.listFolders(), (err: VeruApiError) => {
      assert.ok(err instanceof VeruApiError);
      assert.equal(err.code, "insufficient_scope");
      assert.equal(err.status, 403);
      assert.equal(err.requestId, "req_err");
      assert.equal(err.isPermissionProblem, true);
      assert.equal(err.isRateLimited, false);
      return true;
    });
  });

  it("marks a rate limit as one", async () => {
    const { api } = client(() => fail(429, "rate_limited"));
    await assert.rejects(api.mail.listFolders(), (err: VeruApiError) => {
      assert.equal(err.isRateLimited, true);
      return true;
    });
  });

  it("survives a body that is not JSON", async () => {
    // A proxy or an error page answered instead of the API. Saying so beats a
    // parse error nobody can act on.
    const { api } = client(() => new Response("<html>502</html>", { status: 502 }));

    await assert.rejects(api.mail.listFolders(), (err: VeruApiError) => {
      assert.equal(err.code, "unreadable_response");
      assert.equal(err.status, 502);
      return true;
    });
  });

  it("survives an error body with no error object", async () => {
    const { api } = client(() => Response.json({}, { status: 500 }));
    await assert.rejects(api.mail.listFolders(), (err: VeruApiError) => {
      assert.equal(err.code, "unknown_error");
      assert.match(err.message, /answered 500/);
      assert.deepEqual(err.details, []);
      return true;
    });
  });

  it("carries field details when the API says which field was wrong", async () => {
    const { api } = client(() =>
      Response.json(
        {
          error: {
            code: "validation_error",
            message: "Bad request.",
            request_id: "r",
            details: [{ field: "to", message: "required" }],
          },
        },
        { status: 422 }
      )
    );

    await assert.rejects(api.mail.send({ to: [], subject: "", text: "" }), (err: VeruApiError) => {
      assert.equal(err.details[0]!.field, "to");
      return true;
    });
  });
});

describe("retries", () => {
  it("retries a 429 and succeeds", async () => {
    const { api, seen } = client(
      ({ attempt }) => (attempt === 0 ? fail(429, "rate_limited") : ok([{ id: "f1" }])),
      { maxRetries: 2 }
    );

    const folders = await api.mail.listFolders();
    assert.equal(folders.length, 1);
    assert.equal(seen.length, 2);
  });

  it("retries a 500 and gives up after the limit", async () => {
    const { api, seen } = client(() => fail(500, "internal_error"), { maxRetries: 2 });

    await assert.rejects(api.mail.listFolders(), (err: VeruApiError) => {
      assert.equal(err.status, 500);
      return true;
    });
    // The first attempt plus two retries.
    assert.equal(seen.length, 3);
  });

  it("never retries a refusal, which would fail the same way every time", async () => {
    const { api, seen } = client(() => fail(403, "insufficient_scope"), { maxRetries: 3 });

    await assert.rejects(api.mail.listFolders());
    assert.equal(seen.length, 1);
  });

  it("never retries a 400", async () => {
    const { api, seen } = client(() => fail(400, "malformed_request"), { maxRetries: 3 });
    await assert.rejects(api.mail.listFolders());
    assert.equal(seen.length, 1);
  });

  it("honours Retry-After in seconds", async () => {
    const started = Date.now();
    const { api } = client(
      ({ attempt }) =>
        attempt === 0 ? fail(429, "rate_limited", { "Retry-After": "0" }) : ok([]),
      { maxRetries: 1 }
    );

    await api.mail.listFolders();
    // Zero seconds means immediately; the point is the header was parsed and
    // used rather than ignored in favour of the backoff.
    assert.ok(Date.now() - started < 400);
  });

  it("honours Retry-After as an HTTP date", async () => {
    const { api, seen } = client(
      ({ attempt }) =>
        attempt === 0
          ? fail(429, "rate_limited", { "Retry-After": new Date(Date.now() - 1000).toUTCString() })
          : ok([]),
      { maxRetries: 1 }
    );

    await api.mail.listFolders();
    assert.equal(seen.length, 2);
  });

  it("ignores a Retry-After it cannot parse", async () => {
    const { api, seen } = client(
      ({ attempt }) =>
        attempt === 0 ? fail(429, "rate_limited", { "Retry-After": "soon" }) : ok([]),
      { maxRetries: 1 }
    );

    await api.mail.listFolders();
    assert.equal(seen.length, 2);
  });

  it("retries a network failure", async () => {
    let attempt = 0;
    const api = new VeruApi({
      apiKey: "k",
      baseUrl: "https://api.test",
      maxRetries: 1,
      fetch: () => {
        if (attempt++ === 0) return Promise.reject(new Error("ECONNRESET"));
        return Promise.resolve(ok([{ id: "f1" }]));
      },
    });

    const folders = await api.mail.listFolders();
    assert.equal(folders.length, 1);
    assert.equal(attempt, 2);
  });

  it("gives up on a network failure that keeps happening", async () => {
    const api = new VeruApi({
      apiKey: "k",
      baseUrl: "https://api.test",
      maxRetries: 1,
      fetch: () => Promise.reject(new Error("ECONNRESET")),
    });

    await assert.rejects(api.mail.listFolders(), /ECONNRESET/);
  });

  it("stops when the caller aborts, rather than retrying past them", async () => {
    const controller = new AbortController();
    controller.abort();

    const api = new VeruApi({
      apiKey: "k",
      baseUrl: "https://api.test",
      maxRetries: 3,
      fetch: () => Promise.reject(new Error("aborted")),
    });

    await assert.rejects(
      api.request("GET", "/v1/folders", { signal: controller.signal }),
      /aborted/
    );
  });
});

describe("pagination", () => {
  it("follows the cursor to the end", async () => {
    const pages = [
      { data: [{ id: "1" }], cursor: "c2" },
      { data: [{ id: "2" }], cursor: "c3" },
      { data: [{ id: "3" }], cursor: undefined },
    ];

    const { api, seen } = client(({ attempt }) => {
      const page = pages[attempt]!;
      return ok(page.data, { next_cursor: page.cursor });
    });

    const ids: string[] = [];
    for await (const item of api.mail.messages({ folder_id: "f1" })) {
      ids.push((item as any).id);
    }

    assert.deepEqual(ids, ["1", "2", "3"]);
    assert.equal(seen.length, 3);
    // The first request carries no cursor; the later ones carry the previous
    // page's.
    assert.equal(seen[0]!.url.searchParams.has("cursor"), false);
    assert.equal(seen[1]!.url.searchParams.get("cursor"), "c2");
    assert.equal(seen[2]!.url.searchParams.get("cursor"), "c3");
  });

  it("keeps the caller's filters on every page", async () => {
    const { api, seen } = client(({ attempt }) =>
      ok([{ id: String(attempt) }], { next_cursor: attempt === 0 ? "c2" : undefined })
    );

    for await (const _ of api.mail.messages({ folder_id: "f1", unread: true })) {
      // drain
    }

    for (const call of seen) {
      assert.equal(call.url.searchParams.get("folder_id"), "f1");
      assert.equal(call.url.searchParams.get("unread"), "true");
    }
  });

  it("handles an empty first page", async () => {
    const { api } = client(() => ok([]));

    const items = [];
    for await (const item of api.mail.messages({ folder_id: "f1" })) items.push(item);
    assert.equal(items.length, 0);
  });

  it("handles a page whose data is missing entirely", async () => {
    const { api } = client(() =>
      Response.json({ meta: { request_id: "r", timestamp: "t" } })
    );

    const items = [];
    for await (const item of api.mail.messages({ folder_id: "f1" })) items.push(item);
    assert.equal(items.length, 0);
  });
});

describe("the AbortSignal.any fallback", () => {
  /**
   * AbortSignal.any arrived in Node 20.3, and this client supports 18. The
   * fallback is therefore real code that a modern runtime never reaches, so
   * the only way to test it is to take the modern one away.
   */
  function withoutAbortSignalAny(run: () => Promise<void>) {
    const original = (AbortSignal as any).any;
    delete (AbortSignal as any).any;
    return run().finally(() => {
      if (original) (AbortSignal as any).any = original;
    });
  }

  it("still aborts when the caller's signal fires", async () => {
    await withoutAbortSignalAny(async () => {
      const controller = new AbortController();

      const api = new VeruApi({
        apiKey: "k",
        baseUrl: "https://api.test",
        maxRetries: 0,
        fetch: (_input: any, init: any) =>
          new Promise((_resolve, reject) => {
            init.signal.addEventListener("abort", () => reject(new Error("aborted")), {
              once: true,
            });
            queueMicrotask(() => controller.abort());
          }),
      });

      await assert.rejects(
        api.request("GET", "/v1/folders", { signal: controller.signal }),
        /aborted/
      );
    });
  });

  it("is already aborted when the caller's signal was", async () => {
    await withoutAbortSignalAny(async () => {
      const controller = new AbortController();
      controller.abort();

      let sawAborted = false;
      const api = new VeruApi({
        apiKey: "k",
        baseUrl: "https://api.test",
        maxRetries: 0,
        fetch: (_input: any, init: any) => {
          sawAborted = init.signal.aborted;
          return Promise.reject(new Error("aborted"));
        },
      });

      await assert.rejects(
        api.request("GET", "/v1/folders", { signal: controller.signal }),
        /aborted/
      );
      assert.equal(sawAborted, true, "the combined signal should already be aborted");
    });
  });
});

describe("defaults", () => {
  /**
   * The default branches. Each one is a value somebody gets by not passing
   * anything, so each is worth one line to prove it is the value intended
   * rather than whatever fell out.
   */
  it("falls back to the production API and the platform fetch", async () => {
    const original = globalThis.fetch;
    let asked: string | undefined;

    globalThis.fetch = ((input: any) => {
      asked = String(input);
      return Promise.resolve(Response.json({ data: [], meta: { request_id: "r", timestamp: "t" } }));
    }) as typeof fetch;

    try {
      // No baseUrl, no fetch, no timeout, no maxRetries: every default taken.
      const api = new VeruApi({ apiKey: "k" });
      await api.mail.listFolders();
      assert.equal(asked, "https://api.veruapis.com/v1/folders");
    } finally {
      globalThis.fetch = original;
    }
  });
});
