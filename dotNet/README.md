# VeruSuite.Api (.NET)

Not written yet.

## What it will be

The same shape as [`../node`](../node): a hand-written client over the API
described in [`../spec/openapi.yaml`](../spec/openapi.yaml). No generated code.

```csharp
var api = new VeruApiClient(Environment.GetEnvironmentVariable("VERUAPIS_KEY"));

var folders = await api.Mail.ListFoldersAsync();

await api.Mail.SendAsync(new SendMessage {
    To = ["ops@example.com"],
    Subject = "Nightly report",
    Text = "All green.",
});
```

## The bar it has to meet

The same as every other client here, because these are the parts people hit
rather than the endpoint list:

- **Idempotency.** Every write carries an `Idempotency-Key` unless the caller
  supplies one. A retry that sends the same email twice is the failure that
  actually happens.
- **Retries.** Only `429`, `408` and `5xx`, honouring `Retry-After` when the
  server sends one and exponential backoff with jitter otherwise. A `400` or a
  `403` is never retried; it fails the same way every time.
- **Errors.** One exception type carrying the code, status, request id and any
  field details. Callers branch on the code, never on the message.
- **Paging.** Cursor, never an offset: rows arriving mid-walk shift an offset
  and a page gets skipped. `IAsyncEnumerable`, so a caller does not write the
  loop.
- **A spec check** that fails when the client calls a route the API does not
  serve, and warns about endpoints it has not wrapped. See
  [`../node/scripts/check-spec.mjs`](../node/scripts/check-spec.mjs).
- **Tests above 95%**, enforced with Coverlet thresholds so a regression fails
  CI.

Take an `HttpClient` in the constructor rather than creating one, so callers can
use `IHttpClientFactory` and their own handlers.

Needs the .NET 8 SDK or later. Published to NuGet as `VeruSuite.Api`.
