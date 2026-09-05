# veruapis (Python)

Not written yet.

## What it will be

The same shape as [`../node`](../node): a hand-written client over the API
described in [`../spec/openapi.yaml`](../spec/openapi.yaml). No generated code.

```python
from veruapis import VeruApi

api = VeruApi(api_key=os.environ["VERUAPIS_KEY"])

for folder in api.mail.list_folders():
    print(folder.name, folder.unread_count)

api.mail.send(to=["ops@example.com"], subject="Nightly report", text="All green.")
```

## The bar it has to meet

Whoever writes this should match what the Node client already does, because
these are the parts people hit rather than the endpoint list:

- **Idempotency.** Every write carries an `Idempotency-Key` unless the caller
  supplies one. A retry that sends the same email twice is the failure that
  actually happens.
- **Retries.** Only `429`, `408` and `5xx`, honouring `Retry-After` when the
  server sends one and exponential backoff with jitter otherwise. A `400` or a
  `403` is never retried; it fails the same way every time.
- **Errors.** One exception type carrying `code`, `status`, `request_id` and any
  field details. Callers branch on `code`, never on the message.
- **Paging.** Cursor, never an offset: rows arriving mid-walk shift an offset
  and a page gets skipped.
- **A spec check** that fails when the client calls a route the API does not
  serve, and warns about endpoints it has not wrapped. See
  [`../node/scripts/check-spec.mjs`](../node/scripts/check-spec.mjs).
- **Tests above 95%** of statements, branches, functions and lines, with the
  threshold enforced by the runner so a regression fails CI.

Needs Python 3.9 or later. Published to PyPI as `veruapis`.
