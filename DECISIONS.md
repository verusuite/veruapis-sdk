# Decisions

Why these clients are the way they are, and what is still open. Written for
whoever picks this up next, including the person who wrote it.

Each entry is a decision that could reasonably have gone the other way. A thing
with only one sensible answer is not in here.

---

## Both new clients

### D-1. No runtime dependencies, in any language

Python installs nothing. `.NET` references no package. Go and Node already had
this property and it is now four for four.

The reason is stated in the Go client and holds everywhere: **every dependency
in a client library becomes one in every program that installs it.** A web
service that pulls in this SDK should not thereby acquire an opinion about which
HTTP library it uses, or a transitive CVE, or a version conflict with the one it
had already chosen.

The cost is real and was paid twice:

- Python uses `urllib.request` rather than `requests` or `httpx`. `urllib` is
  clumsier: it raises on 4xx and 5xx rather than returning them, so the error
  body has to be read out of the exception, and there is no connection pooling.
  For a client that makes a handful of calls per process this is the right
  trade. For one making thousands per second it would not be.
- .NET uses `HttpClient` and `System.Text.Json`, which is no hardship, but it
  means no `Polly` for retries and no `Refit` for the surface. Both are written
  by hand here, which is about sixty lines.

Test-time tools are not covered by this rule. Python uses `coverage`, .NET uses
`xunit` and `coverlet`, and none of that reaches a consumer.

### D-2. The spec check runs the client rather than reading it

All four clients collect the routes they call by **invoking every method
against a recording transport** and comparing what arrived to
`spec/openapi.json`.

Node was the exception until the spreadsheet methods landed, and they are what
showed the cost. `scripts/check-spec.mjs` scanned the source with a regular
expression — the approach the Go client tried first and abandoned, for the
reason `go/spec_test.go` records: a path built by concatenation gives up only
its first literal to a regex. The spreadsheet paths go through a helper that
escapes the range, so the scanner reported three routes as unwrapped that the
client had wrapped, and would equally have passed a truncated prefix matching a
route nobody wrote.

It now runs the client, like the other three. The one cost is that it reads
`dist/`, so the build has to be current; `prepublishOnly` already builds.

### D-3. The clients are checked against each other, not only against the spec

Each new client has a test asserting that every route the **Go** client calls is
also called by it. Four clients drifting apart is the failure this repository
exists to prevent, and the spec check alone does not catch it: a client that
wrapped nothing at all would pass, since it would call no undocumented route.

Go is the reference because it was written first and is the most complete.

---

## Python

### D-4. Targets 3.9, which is past end of life

`requires-python = ">=3.9"`, because the website and the repository README
already promised that and the promise costs little to keep:
`from __future__ import annotations` covers the syntax gap.

Python 3.9 reached end of life in October 2025. **Open: raise the floor to 3.10
or 3.11 at the next minor.** Nothing in the client needs 3.9; it is only there
because it was advertised.

### D-5. Dataclasses, and unknown fields are ignored

`message.subject` is a typo an editor catches. `message["subject"]` is not.

The decoder ignores keys it does not recognise rather than raising. The API adds
fields within a version, and a client that rejected an unfamiliar key would
break every time the server learned something new. A missing key takes the
field's default instead of failing, for the same reason.

`TypedDict` was the alternative and gives no runtime shape at all. Pydantic was
not, because of D-1.

### D-6. `from_`, and `calendars.py`

`from` is a keyword, so the field is `from_` and the wire name is mapped in one
place. The alternative spellings (`from_addr`, `sender`) would have invented a
name the API does not use.

The module is `calendars.py` rather than `calendar.py` so that nothing importing
this has to think about the standard library module of that name. The attribute
is still `api.calendar`, because that is what it is.

### D-7. `send()` takes keywords or a `SendMessage`

```python
api.mail.send(to=["ops@example.com"], subject="Nightly", text="All green.")
api.mail.send(SendMessage(to=["ops@example.com"], subject="Nightly"))
```

Both, because the website already advertises the first and the second is better
when the message is built somewhere other than the call site. Passing both at
once raises rather than silently preferring one, since quietly ignoring half of
what a caller wrote is the worst way to resolve an ambiguity.

---

## .NET

### D-8. Targets `net8.0`

The long-term support release, so a consumer who has not moved to 10 can still
use this. A client library should not be the reason somebody upgrades a runtime.

The **test** project sets `RollForward=Major` so it runs on whatever runtime is
installed. The library is still compiled against net8.0; only the test host
rolls forward.

### D-9. One naming policy instead of an attribute per property

`JsonNamingPolicy.SnakeCaseLower` handles the whole PascalCase-to-snake_case
difference in one line. `[JsonPropertyName]` on ninety properties would say the
same thing ninety times and be wrong on the ninety-first.

`ShapeTests` reads back every field of a message, a message summary, a calendar
and an event for exactly this reason: a policy that silently stopped applying
would leave every multi-word field at its default, which reads as an API that
returned nothing rather than as a client that failed.

### D-10. CS1591 is suppressed

`GenerateDocumentationFile` is on, so the comments that earn their place reach a
consumer's editor. CS1591, which demands a comment on **every** public member,
is off: on a record of wire fields it produces eighty repetitions of "Gets the
Id", and a codebase where most comments say nothing teaches people to stop
reading them.

---

## The one behavioural difference between the clients

### D-11. .NET can send an explicit `false`. Go and Python cannot.

Go uses `omitempty`, which drops a `false` and a `0` along with an empty string.
Python matches it deliberately, so the two put the same bytes on the wire for
the same call.

The consequence: **neither can turn an all-day event back into a timed one.**
`AllDay = false` is indistinguishable from never mentioning it, so the field is
dropped and the update does nothing. The same applies to any future boolean or
numeric field that a caller might want to set back to its zero value.

.NET does not have this problem, because optional fields are nullable and null
is what "not set" means. That is idiomatic C# and I was not willing to introduce
a known bug for the sake of symmetry.

**Open, and the most worthwhile thing in this file.** The fix is to make the
affected fields nullable in Go (`*bool`) and Optional in Python, so that unset
and false are different values everywhere. It is a breaking change to those two
clients, so it wants a major version and should be done to both at once.

Until then: to clear an all-day flag from Go or Python, call the API directly
with `Do` / `request`.

---

## Also worth fixing, found while writing these

### D-12. The Node client's error surface differs from the other three

Go, Python and .NET each expose four questions about a failure: is it auth, a
permission, not found, rate limited. Node exposes two, and as getters rather
than methods:

| | auth | permission | not found | rate limited |
|---|---|---|---|---|
| Go | `IsAuthProblem()` | `IsPermissionProblem()` | `IsNotFound()` | `IsRateLimited()` |
| Python | `is_auth_problem` | `is_permission_problem` | `is_not_found` | `is_rate_limited` |
| .NET | `IsAuthProblem` | `IsPermissionProblem` | `IsNotFound` | `IsRateLimited` |
| Node | — | `isPermissionProblem` | — | `isRateLimited` |

A Node caller has to write `err.status === 401`. The website says "the same
behaviour everywhere", and this is the one place it is not true. It is additive,
so `@verusuite/api@1.1.0` fixes it without breaking anybody.

### D-13. `Flags` versus `MessageFlags`

Go calls the type `Flags`. Node, Python and .NET call it `MessageFlags`. Three
against one, and the longer name is clearer at a use site. Not worth a breaking
change to Go on its own; worth folding into the D-11 major if that happens.

---

## Not done, deliberately

**Neither new client is published.** Python is not on PyPI and .NET is not on
NuGet. Publishing is public and effectively permanent, the accounts are not
mine, and the names should be registered by whoever owns them. Both are ready:
`python -m build` and `dotnet pack` are the only steps left.

The website marks both as written and installable from source rather than
claiming a package that does not exist. A published install command that fails
is worse than no page.
