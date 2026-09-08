# veruapis

Python client for the VeruSuite API: mail, calendar and workspace
administration.

```bash
pip install veruapis
```

Needs Python 3.9 or later. It installs nothing else: every dependency in a
client library becomes one in every program that installs it, and everything
this needs is in the standard library.

> Not on PyPI yet. Until it is, install from a checkout:
>
> ```bash
> pip install ./veruapis-sdks/python
> ```

## Use

```python
import os
from veruapis import VeruApi, VeruApiError

api = VeruApi(api_key=os.environ["VERUAPIS_KEY"])

for folder in api.mail.list_folders():
    print(folder.name, folder.unread_count)
```

The key acts as the person who created it, limited to the permissions they
granted. Nothing here widens it.

### Walking a listing

```python
for message in api.mail.messages(folder_id=[inbox], unread=True):
    print(message.subject)
```

The cursor is followed for you. Cursor rather than an offset, because messages
arriving mid-walk shift an offset and a page gets skipped.

### Sending

```python
api.mail.send(
    to=["ops@example.com"],
    subject="Nightly report",
    text="All green.",
)
```

An `Idempotency-Key` is generated unless you supply one, so a retry cannot send
the same message twice. A `SendMessage` works too, when the message is built
somewhere other than the call site.

The API answers 202: the message is queued and archived in Sent, and delivery
happens afterwards. That is not a promise it arrived, and a failure comes back
later as a bounce.

### Calendar

```python
from veruapis import Event

events, meta = api.calendar.list_events(start="2026-01-01T00:00:00Z",
                                        end="2026-01-31T23:59:59Z")

api.calendar.create_event("cal_1", Event(
    summary="Standup",
    starts_at="2026-01-02T09:00:00Z",
    ends_at="2026-01-02T09:15:00Z",
))
```

Times are UTC. Recurring events arrive already expanded, one entry per
occurrence, which is why the window is required rather than optional.

### When it refuses

```python
try:
    api.mail.send(to=["ops@example.com"], subject="hi")
except VeruApiError as err:
    if err.is_permission_problem:
        raise SystemExit(f"this key is missing {err.code}")
    raise
```

Branch on `err.code`, never on `err.message`. The code is a stable identifier;
the message is written for a person and may be reworded at any time.
`is_auth_problem`, `is_permission_problem`, `is_not_found` and
`is_rate_limited` cover the four questions worth asking.

429, 408 and 5xx are retried for you, after the delay the server asked for. A
400 or a 403 is not, because it would fail the same way however many times it
is sent.

### Anything not wrapped yet

```python
data, meta = api.request("GET", "/v1/mail/settings")
```

The client is allowed to lag the API, and `request` reaches whatever it has not
got to.

## Development

```bash
python -m unittest discover -s tests -t tests
python -m coverage run --rcfile=pyproject.toml -m unittest discover -s tests -t tests
python -m coverage report --rcfile=pyproject.toml
```

`tests/test_spec.py` is the important one: it calls every method against a
recording server and fails if any of them asks for a route the API does not
serve. That is what makes a hand-written client safe rather than merely nicer
to read.

Coverage is held at 95% by `fail_under` in `pyproject.toml`, and currently
sits at 100%.
