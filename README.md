# VeruSuite API SDKs

Client libraries for [api.veruapis.com](https://api.veruapis.com), in Node,
Python, Go and .NET.

## How these are built

Everything here derives from `spec/openapi.yaml`, which is itself generated from
the server's own routing and scope tables:

```
veruapis  ──  veruapis spec  ──▶  spec/openapi.yaml  ──▶  node/ python/ go/ dotNet/
```

The specification is never hand-edited. It cannot describe an endpoint the
server does not serve, because it is built from the tables that route and gate
requests, and a test in the server repository fails if the committed copy has
drifted from what those tables produce.

That matters more than it sounds. A specification maintained by hand goes stale
quietly, and the first person to find out is a developer whose working code
stops matching the documentation. Core's own progress notes flag exactly that
risk in their hand-written specification; this repository exists so we do not
inherit it.

### Written by hand, checked against the specification

Nothing here is generated code. A generator produces types you reach *through*
rather than types you use, and the result is not something anybody wants in
their editor. A hand-written `MessageSummary[]`, and `api.mail.listMessages()`
rather than a raw path, is what a developer actually has to live inside.

The cost of writing by hand is drift, so it is paid for rather than ignored.
Each language has a **spec check** that reads the specification and compares it
against the client:

- a route the SDK calls that the API does not serve is an **error**, because it
  is a 404 waiting for whoever calls that method,
- an endpoint the API serves that the SDK has not wrapped is a **warning**,
  because the SDK is allowed to lag and the raw `request()` reaches it anyway.

It runs in CI, alongside the tests. That is what makes hand-written safe rather
than merely nicer.

## Repository layout

| Path | What it is |
|---|---|
| `spec/openapi.yaml` | The API description. Copied from the server repository at generation time. |
| `scripts/check.ps1` | Checks every SDK against the specification. |
| `node/` | TypeScript and JavaScript, for npm. **Written.** |
| `python/` | Python, for PyPI. Not written yet; the folder holds the brief. |
| `go/` | Go module. **Written.** |
| `dotNet/` | .NET, for NuGet. Not written yet; the folder holds the brief. |

## After the API changes

Refresh the specification, then check every SDK against it:

```powershell
cd ..\veruapis
go run .\cmd\veruapis spec ..\veruapis-sdks\spec\openapi.yaml
```

### What each language needs

| Language | Needs | Tests | Spec check |
|---|---|---|---|
| Node | Node 18+ | `npm test` | `npm run check-spec` |
| Python | Python 3.9+ | not written yet | not written yet |
| Go | Go 1.23+ | `go test ./...` | included in `go test` |
| .NET | .NET SDK 8+ | not written yet | not written yet |

No code generator, and therefore no Java. The specification is read by the spec
check, not fed to a generator.

## Publishing

Each language publishes to its own registry, except Go, which resolves straight
from this repository and needs nothing.

```bash
cd node && npm publish        # prepublishOnly runs the spec check, coverage and build
```

`prepublishOnly` is what stops a stale `dist/` shipping: the tarball carries
whatever was last built, and "whatever was last built" is not a release
process.

**npm cannot install this from git.** It looks for `package.json` at the
repository root, and the package is in `node/`. That is the one real cost of
keeping four languages in one repository, and it only applies until the package
is published. A packed tarball covers the gap in the meantime.

## Tagging a release

Two tags, and both are needed.

```bash
git tag -a v1.0.0    -m "..."   # the repository, and what npm publishes from
git tag -a go/v1.0.0 -m "..."   # the Go module
git push --tags
```

The Go module lives in a subdirectory, so its version tag has to carry that
directory as a prefix. A plain `v1.0.0` is invisible to
`go get github.com/verusuite/veruapis-sdk/go@v1.0.0`, which then falls back to
a pseudo-version from the default branch and gives no stable release at all.

## Versioning

SDK versions are independent of the API version. The API is `v1` and stays `v1`;
an SDK release is a release of this client code.

- **Patch** for a fix that changes no signature.
- **Minor** for new endpoints or optional fields, which is what most
  specification updates produce.
- **Major** only for a breaking change to this client's own surface.

Each release records the specification it was generated from, so a bug report
can be tied to an exact API description rather than to a date.

## Licence

MIT. See [LICENSE](LICENSE).

MIT rather than Apache-2.0 because that is what a client library is expected to
carry: Stripe, Twilio, SendGrid and Slack all ship theirs under it, and nobody
wants to read a licence to call an API. Apache-2.0's explicit patent grant is
the reason to choose it instead, and is worth revisiting if these ever carry
more than thin client code.

Copyright is held by **North Wave MB**.
