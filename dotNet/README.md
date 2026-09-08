# VeruSuite.Api

.NET client for the VeruSuite API: mail, calendar and workspace
administration.

```bash
dotnet add package VeruSuite.Api
```

Targets `net8.0`, the long-term support release, so a consumer who has not
moved to 10 can still use it. It references no package: every dependency in a
client library becomes one in every program that installs it, and `HttpClient`
and `System.Text.Json` are all this needs.

> Not on NuGet yet. Until it is, reference the project directly:
>
> ```bash
> dotnet add reference ../veruapis-sdks/dotNet/src/VeruSuite.Api/VeruSuite.Api.csproj
> ```

## Use

```csharp
using VeruSuite.Api;

using var api = new VeruApiClient(Environment.GetEnvironmentVariable("VERUAPIS_KEY")!);

foreach (var folder in await api.Mail.ListFoldersAsync())
    Console.WriteLine($"{folder.Name} {folder.UnreadCount}");
```

The key acts as the person who created it, limited to the permissions they
granted. Nothing here widens it.

In a program that already has an `HttpClient`, hand it over. Connection
pooling, proxies and timeouts are usually decided once for a whole process
rather than per library, and a supplied client is not disposed by this one:

```csharp
var api = new VeruApiClient(key, new VeruApiClientOptions { HttpClient = shared });
```

### Walking a listing

```csharp
await foreach (var message in api.Mail.MessagesAsync(new() { FolderId = [inbox], Unread = true }))
    Console.WriteLine(message.Subject);
```

The cursor is followed for you. Cursor rather than an offset, because messages
arriving mid-walk shift an offset and a page gets skipped.

### Sending

```csharp
await api.Mail.SendAsync(new SendMessage
{
    To = ["ops@example.com"],
    Subject = "Nightly report",
    Text = "All green.",
});
```

An `Idempotency-Key` is generated unless you supply one, so a retry cannot send
the same message twice.

The API answers 202: the message is queued and archived in Sent, and delivery
happens afterwards. That is not a promise it arrived, and a failure comes back
later as a bounce.

### Calendar

```csharp
var (events, meta) = await api.Calendar.ListEventsAsync(new()
{
    Start = "2026-01-01T00:00:00Z",
    End = "2026-01-31T23:59:59Z",
});

await api.Calendar.CreateEventAsync("cal_1", new Event
{
    Summary = "Standup",
    StartsAt = "2026-01-02T09:00:00Z",
    EndsAt = "2026-01-02T09:15:00Z",
});
```

Times are UTC. Recurring events arrive already expanded, one entry per
occurrence, which is why the window is required rather than optional.

Optional fields are nullable, and null means "not set" and is left out of the
body. That is what makes a partial update partial, and it is also why
`AllDay = false` can be sent at all: an unset boolean and a deliberate false
are different requests.

### When it refuses

```csharp
try
{
    await api.Mail.SendAsync(message);
}
catch (VeruApiException err) when (err.IsPermissionProblem)
{
    Console.Error.WriteLine($"this key is missing {err.Code}");
}
```

Branch on `err.Code`, never on `err.Message`. The code is a stable identifier;
the message is written for a person and may be reworded at any time.
`IsAuthProblem`, `IsPermissionProblem`, `IsNotFound` and `IsRateLimited` cover
the four questions worth asking.

429, 408 and 5xx are retried for you, after the delay the server asked for. A
400 or a 403 is not, because it would fail the same way however many times it
is sent.

### Anything not wrapped yet

```csharp
var (settings, meta) = await api.SendAsync<JsonElement>(HttpMethod.Get, "/v1/mail/settings");
```

The client is allowed to lag the API, and `SendAsync` reaches whatever it has
not got to.

## Development

```bash
cd tests/VeruSuite.Api.Tests
dotnet test
```

`SpecTests` is the important one: it calls every method against a recording
handler and fails if any of them asks for a route the API does not serve. That
is what makes a hand-written client safe rather than merely nicer to read.

Coverage is held at 95% by coverlet, configured in the test project, and
currently sits at 100% of lines and methods.
