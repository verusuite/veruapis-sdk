# @veruapis/sdk

TypeScript and JavaScript client for the VeruSuite API.

```bash
npm install @verusuite/api
```

Installing from the git repository does not work and cannot be made to: npm
looks for `package.json` at the repository root, and this package lives in
`node/`. Until it is published, install a packed tarball instead:

```bash
cd veruapis-sdks/node && npm pack     # builds verusuite-api-<version>.tgz
npm install ../veruapis-sdks/node/verusuite-api-0.1.0.tgz
```

## Use

```ts
import { VeruApi, VeruApiError } from "@verusuite/api";

const api = new VeruApi({ apiKey: process.env.VERUAPIS_KEY! });

const folders = await api.mail.listFolders();

try {
  await api.mail.send({
    to: ["ops@example.com"],
    subject: "Nightly report",
    text: "All green.",
  });
} catch (err) {
  if (err instanceof VeruApiError && err.isPermissionProblem) {
    console.error(`This key is missing a permission: ${err.code}`);
  }
}
```

The envelope is unwrapped for you. Use `requestEnvelope` when you want the
request id or the pagination cursor.

## Paging

```ts
for await (const message of api.mail.messages({ folder_id })) {
  console.log(message.subject);
}
```

Cursor rather than page number: rows arriving mid-walk shift an offset and
would make a page get skipped.

## What it does for you

- **Idempotency.** Every write carries an `Idempotency-Key` unless you supply
  one, so a retry cannot send the same email twice.
- **Retries.** Only on 429, 408 and 5xx, with the server's `Retry-After` when it
  sends one, and exponential backoff with jitter otherwise. A 400 or a 403 is
  never retried; it will fail the same way every time.
- **Errors.** `VeruApiError` carries `code`, `status`, `requestId` and any field
  details. Branch on `code`, never on `message`, which is written for a person
  and may be reworded.

## Anything not wrapped yet

`request()` reaches every endpoint, wrapped or not:

```ts
const settings = await api.request("GET", "/v1/mail/settings");
```

## Types

Written by hand, in `src/types.ts`. Nothing here is generated, and the drift
that would otherwise cost is checked instead:

```bash
npm test          # unit tests, with coverage
npm run check-spec
```
