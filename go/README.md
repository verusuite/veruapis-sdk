# veruapis (Go)

Client for the VeruSuite API: mail, calendar and workspace administration.

```bash
go get github.com/verusuite/veruapis-sdk/go
```

Needs Go 1.23 or later, for range-over-func. No dependencies outside the
standard library: every dependency in a client library becomes one in every
program that imports it.

## Use

```go
api := veruapis.New(os.Getenv("VERUAPIS_KEY"))

folders, err := api.Mail.ListFolders(ctx)

_, err = api.Mail.Send(ctx, veruapis.SendMessage{
    To:      []string{"ops@example.com"},
    Subject: "Nightly report",
    Text:    "All green.",
})
```

Every call takes a `context.Context`, and the client is safe for concurrent use.

## Errors

```go
var apiErr *veruapis.Error
if errors.As(err, &apiErr) && apiErr.IsPermissionProblem() {
    log.Fatalf("this key is missing %s", apiErr.Code)
}
```

Branch on `Code`, never on `Message`. The code is a stable identifier; the
message is written for a person and may be reworded.

`IsPermissionProblem`, `IsRateLimited`, `IsNotFound` and `IsAuthProblem` cover
the cases worth handling differently.

## Paging

```go
for msg, err := range api.Mail.Messages(ctx, veruapis.ListMessagesOptions{
    FolderID: []string{folderID},
}) {
    if err != nil {
        return err
    }
    fmt.Println(msg.Subject)
}
```

Cursor rather than an offset: messages arriving mid-walk shift an offset and a
page gets skipped, which is data loss that looks like nothing at all.

The error is a value in the sequence rather than something returned at the end,
because a walk that fails on page four has already yielded three and the caller
needs to know where it stopped.

## What it does for you

- **Idempotency.** Every write carries an `Idempotency-Key` unless you supply
  one, and the same key survives every retry. A retry that sends the same email
  twice is the failure that actually happens.
- **Retries.** Only `429`, `408` and `5xx`, honouring `Retry-After` in seconds
  or as a date, and exponential backoff with jitter otherwise. A `400` or a
  `403` is never retried; it fails the same way every time.
- **Cancellation.** A cancelled context stops the retry loop rather than
  outliving the caller.

## Anything not wrapped yet

`Do` reaches every endpoint, wrapped or not:

```go
settings, _, err := veruapis.Do[map[string]any](ctx, api, veruapis.Request{
    Method: http.MethodGet,
    Path:   "/v1/mail/settings",
})
```

## Options

```go
api := veruapis.New(key,
    veruapis.WithBaseURL("https://api.veruapis.com"),
    veruapis.WithHTTPClient(myClient),  // use your own pooling and timeouts
    veruapis.WithMaxRetries(0),         // or none at all
)
```

## Development

```bash
go test ./...                                  # includes the spec check
go test -coverprofile=coverage.out ./...
go tool cover -html=coverage.out
```

Types are written by hand. The cost of that is drift, so it is paid for:
`spec_test.go` calls every method against a recording server and compares the
routes with `../spec/openapi.json`. A route this client calls that the API does
not serve fails the build; an endpoint not wrapped yet is reported, not failed.

Coverage is held above 95% by `../scripts/check.ps1`.
