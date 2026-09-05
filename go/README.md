# veruapis (Go)

Not written yet.

## What it will be

The same shape as [`../node`](../node): a hand-written client over the API
described in [`../spec/openapi.yaml`](../spec/openapi.yaml). No generated code.

```go
api := veruapis.New(os.Getenv("VERUAPIS_KEY"))

folders, err := api.Mail.ListFolders(ctx)
if err != nil {
    var apiErr *veruapis.Error
    if errors.As(err, &apiErr) && apiErr.IsPermissionProblem() {
        log.Fatalf("this key is missing %s", apiErr.Code)
    }
}
```

The import path is fixed by the repository name and cannot change later without
breaking every caller:

```
github.com/verusuite/veruapis-sdk/go
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
- **Errors.** One error type carrying the code, status, request id and any
  field details, reachable through `errors.As`. Callers branch on the code,
  never on the message.
- **Paging.** Cursor, never an offset: rows arriving mid-walk shift an offset
  and a page gets skipped. An iterator, so a caller does not write the loop.
- **A spec check** that fails when the client calls a route the API does not
  serve, and warns about endpoints it has not wrapped. See
  [`../node/scripts/check-spec.mjs`](../node/scripts/check-spec.mjs).
- **Tests above 95%**, enforced with `go test -cover -coverprofile` and a
  threshold check so a regression fails CI.

Every call takes a `context.Context`, and the client is safe for concurrent use.

Needs Go 1.22 or later.
