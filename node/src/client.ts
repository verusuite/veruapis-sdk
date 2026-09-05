import { CalendarApi, Mail } from "./resources.js";
import type { Envelope } from "./types.js";

/**
 * A client for the VeruSuite API.
 *
 * Written by hand, types included. A generated client produces types you reach
 * through rather than types you use, and the surface that matters here is small
 * and stable: one envelope, one error shape, cursor paging, bearer auth.
 *
 * The cost of writing types by hand is drift, so it is paid for rather than
 * ignored: `npm run check-spec` compares them against the API's own description
 * and fails when the two disagree.
 */

export type ClientOptions = {
  /** A key created at veruapis.com. It acts as you, limited to its permissions. */
  apiKey: string;

  /** Defaults to the production API. */
  baseUrl?: string;

  /** Milliseconds before a request is abandoned. Defaults to 30 seconds. */
  timeout?: number;

  /**
   * How many times to retry a request the server said to retry.
   *
   * Only 429 and 5xx, and only for requests that are safe to repeat. Defaults
   * to 2, which covers a rate limit and a transient failure without turning a
   * broken call into a storm.
   */
  maxRetries?: number;

  /** Swap in a different fetch, for tests or a proxy. */
  fetch?: typeof globalThis.fetch;
};

/**
 * A refusal from the API.
 *
 * Branch on `code`, never on `message`. The code is a stable identifier; the
 * message is written for a person and may be reworded at any time.
 */
export class VeruApiError extends Error {
  readonly code: string;
  readonly status: number;
  readonly requestId: string;
  readonly details: { field: string; message?: string }[];

  /** When the API said when to try again, in milliseconds. */
  readonly retryAfterMs?: number;

  constructor(
    status: number,
    body: { error?: { code?: string; message?: string; request_id?: string; details?: unknown[] } },
    retryAfterMs?: number
  ) {
    const err = body?.error;
    super(err?.message ?? `The API answered ${status}.`);

    this.name = "VeruApiError";
    this.status = status;
    this.code = err?.code ?? "unknown_error";
    this.requestId = err?.request_id ?? "";
    this.details = (err?.details as this["details"]) ?? [];
    this.retryAfterMs = retryAfterMs;
  }

  /** True when the key is valid but was not granted what this call needs. */
  get isPermissionProblem(): boolean {
    return this.status === 403;
  }

  /** True when waiting and retrying is the right response. */
  get isRateLimited(): boolean {
    return this.status === 429;
  }
}

export type Method = "GET" | "POST" | "PUT" | "PATCH" | "DELETE";

export type RequestOptions = {
  query?: Record<string, string | number | boolean | string[] | undefined>;
  body?: unknown;

  /**
   * Makes a retried write safe: the server returns the first response rather
   * than performing the work twice. Generated for every write unless you
   * supply one, because a retry that sends two emails is the failure people
   * actually hit.
   */
  idempotencyKey?: string;

  signal?: AbortSignal;
};

/**
 * What the grouped methods call.
 *
 * An interface so resources.ts depends on the shape of a transport rather than
 * on the client itself, which keeps the two testable apart.
 */
export interface Transport {
  request<T>(method: Method, path: string, options?: RequestOptions): Promise<T>;
  requestEnvelope<T>(method: Method, path: string, options?: RequestOptions): Promise<Envelope<T>>;
  paginate<T>(path: string, options?: RequestOptions): AsyncGenerator<T>;
}

export class VeruApi implements Transport {
  /** Mail: folders, messages, drafts, sending. */
  readonly mail: Mail;

  /** Calendars, events and availability. */
  readonly calendar: CalendarApi;

  readonly #apiKey: string;
  readonly #baseUrl: string;
  readonly #timeout: number;
  readonly #maxRetries: number;
  readonly #fetch: typeof globalThis.fetch;

  constructor(options: ClientOptions) {
    if (!options?.apiKey) {
      throw new Error("An apiKey is required. Create one at veruapis.com.");
    }

    this.#apiKey = options.apiKey;
    this.#baseUrl = (options.baseUrl ?? "https://api.veruapis.com").replace(/\/+$/, "");
    this.#timeout = options.timeout ?? 30_000;
    this.#maxRetries = options.maxRetries ?? 2;
    this.#fetch = options.fetch ?? globalThis.fetch;

    this.mail = new Mail(this);
    this.calendar = new CalendarApi(this);
  }

  /**
   * Makes one request and returns the unwrapped `data`.
   *
   * The envelope is unwrapped here rather than handed to the caller, because
   * every response has one and reaching through `.data` at every call site is
   * noise. `requestEnvelope` returns the whole thing when the request id or the
   * cursor is wanted.
   */
  async request<T>(method: Method, path: string, options: RequestOptions = {}): Promise<T> {
    const envelope = await this.requestEnvelope<T>(method, path, options);
    return envelope.data;
  }

  /** As `request`, but returns the envelope, including `meta.next_cursor`. */
  async requestEnvelope<T>(
    method: Method,
    path: string,
    options: RequestOptions = {}
  ): Promise<Envelope<T>> {
    const url = new URL(this.#baseUrl + path);

    for (const [key, value] of Object.entries(options.query ?? {})) {
      if (value === undefined) continue;
      // A repeatable parameter is sent as repeated keys. Joining with a comma
      // would be read by the API as one value.
      if (Array.isArray(value)) {
        for (const item of value) url.searchParams.append(key, String(item));
      } else {
        url.searchParams.set(key, String(value));
      }
    }

    const headers: Record<string, string> = {
      Authorization: `Bearer ${this.#apiKey}`,
      Accept: "application/json",
    };
    if (options.body !== undefined) {
      headers["Content-Type"] = "application/json";
    }
    // Writes get one whether or not the caller thought about it.
    if (method !== "GET" && method !== "DELETE") {
      headers["Idempotency-Key"] = options.idempotencyKey ?? crypto.randomUUID();
    }

    let lastError: unknown;

    for (let attempt = 0; attempt <= this.#maxRetries; attempt++) {
      // A timeout per attempt, not per call, so a retry gets its own budget.
      const timer = new AbortController();
      const timeout = setTimeout(() => timer.abort(), this.#timeout);
      const signal = options.signal
        ? anySignal([options.signal, timer.signal])
        : timer.signal;

      try {
        const res = await this.#fetch(url, {
          method,
          headers,
          body: options.body === undefined ? undefined : JSON.stringify(options.body),
          signal,
        });

        if (res.ok) {
          if (res.status === 204) return { data: undefined as T, meta: emptyMeta() };
          return (await res.json()) as Envelope<T>;
        }

        const retryAfterMs = retryAfter(res);
        const error = new VeruApiError(res.status, await safeJson(res), retryAfterMs);

        // Retried only when the server said so. A 400 or a 403 will fail the
        // same way however many times it is sent.
        if (!shouldRetry(res.status) || attempt === this.#maxRetries) throw error;

        lastError = error;
        await sleep(retryAfterMs ?? backoff(attempt));
      } catch (err) {
        if (err instanceof VeruApiError) throw err;

        // A network failure or a timeout. Worth one more try; if the caller
        // aborted, it is not.
        if (options.signal?.aborted || attempt === this.#maxRetries) throw err;
        lastError = err;
        await sleep(backoff(attempt));
      } finally {
        clearTimeout(timeout);
      }
    }

    throw lastError;
  }

  /**
   * Walks every page of a list endpoint.
   *
   * Cursor rather than page number, because rows arriving mid-walk shift an
   * offset and would make a page get skipped.
   *
   * ```ts
   * for await (const message of api.paginate("/v1/messages", { query: { folder_id } })) {
   *   ...
   * }
   * ```
   */
  async *paginate<T>(path: string, options: RequestOptions = {}): AsyncGenerator<T> {
    let cursor: string | undefined;

    do {
      const page = await this.requestEnvelope<T[]>("GET", path, {
        ...options,
        query: { ...options.query, ...(cursor ? { cursor } : {}) },
      });

      for (const item of page.data ?? []) yield item;
      cursor = page.meta?.next_cursor;
    } while (cursor);
  }
}

function emptyMeta() {
  return { request_id: "", timestamp: new Date().toISOString() };
}

async function safeJson(res: Response): Promise<any> {
  try {
    return await res.json();
  } catch {
    // A non-JSON body from this API means something upstream of it answered:
    // a proxy, an error page, a misrouted request. Say that rather than
    // reporting a parse error nobody can act on.
    return { error: { code: "unreadable_response", message: `The server answered ${res.status}.` } };
  }
}

/** Retry-After, in milliseconds. Seconds or an HTTP date, per the RFC. */
function retryAfter(res: Response): number | undefined {
  const header = res.headers.get("Retry-After");
  if (!header) return undefined;

  const seconds = Number(header);
  if (!Number.isNaN(seconds)) return Math.max(0, seconds * 1000);

  const when = Date.parse(header);
  return Number.isNaN(when) ? undefined : Math.max(0, when - Date.now());
}

function shouldRetry(status: number): boolean {
  return status === 429 || status === 408 || status >= 500;
}

/** Exponential, with jitter so a fleet of clients does not retry in step. */
function backoff(attempt: number): number {
  return Math.min(2 ** attempt * 500, 8_000) * (0.5 + Math.random() / 2);
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/** AbortSignal.any, with a fallback for runtimes that predate it. */
function anySignal(signals: AbortSignal[]): AbortSignal {
  if (typeof AbortSignal.any === "function") return AbortSignal.any(signals);

  const controller = new AbortController();
  for (const s of signals) {
    if (s.aborted) {
      controller.abort(s.reason);
      break;
    }
    s.addEventListener("abort", () => controller.abort(s.reason), { once: true });
  }
  return controller.signal;
}
