using VeruSuite.Api;
using Xunit;

namespace VeruSuite.Api.Tests;

/// <summary>
/// The wire shapes, read back field by field.
/// </summary>
/// <remarks>
/// C# is PascalCase and this API is snake_case, and one naming policy bridges
/// them for every property at once. That is worth having, and it is also worth
/// checking: a policy that silently stopped applying would leave every
/// multi-word field at its default, which reads as an API that returned
/// nothing rather than as a client that failed.
/// </remarks>
public class ShapeTests
{
    [Fact]
    public async Task Every_field_of_a_message_summary_survives_the_naming_policy()
    {
        using var api = new FakeApi(new Reply(200, Bodies.Envelope(
            """
            [{"id":"m1","folder_id":"f1","thread_id":"t1","message_id":"<x@y>",
              "subject":"hello","from":"a@b.example","date":"2026-01-01T00:00:00Z","size":4096,
              "flags":{"read":true,"starred":true,"answered":true,"draft":true,"deleted":true},
              "labels":["work","urgent"]}]
            """)));

        var (messages, _) = await api.Client().Mail.ListMessagesAsync(new ListMessagesQuery { FolderId = ["f1"] });
        var message = messages[0];

        Assert.Equal("m1", message.Id);
        Assert.Equal("f1", message.FolderId);
        Assert.Equal("t1", message.ThreadId);
        Assert.Equal("<x@y>", message.MessageId);
        Assert.Equal("hello", message.Subject);
        Assert.Equal("a@b.example", message.From);
        Assert.Equal("2026-01-01T00:00:00Z", message.Date);
        Assert.Equal(4096, message.Size);
        Assert.Equal(["work", "urgent"], message.Labels);

        Assert.True(message.Flags.Read);
        Assert.True(message.Flags.Starred);
        Assert.True(message.Flags.Answered);
        Assert.True(message.Flags.Draft);
        Assert.True(message.Flags.Deleted);
    }

    [Fact]
    public async Task Every_field_of_a_message_survives_the_naming_policy()
    {
        using var api = new FakeApi(new Reply(200, Bodies.Envelope(
            """
            {"id":"m1","message_id":"<x@y>","subject":"hello","date":"2026-01-01T00:00:00Z",
             "from":{"email":"a@b.example","name":"A"},
             "to":[{"email":"c@d.example"}],"cc":[{"email":"e@f.example"}],
             "text":"plain","html":"<p>rich</p>","headers":{"X-Thing":"1"},
             "attachments":[{"part_id":"2","filename":"r.pdf","content_type":"application/pdf",
                             "size":10,"inline":true}]}
            """)));

        var message = (await api.Client().Mail.GetMessageAsync("m1"))!;

        Assert.Equal("<x@y>", message.MessageId);
        Assert.Equal("2026-01-01T00:00:00Z", message.Date);
        Assert.Equal("plain", message.Text);
        Assert.Equal("<p>rich</p>", message.Html);
        Assert.Equal("e@f.example", message.Cc[0].Email);

        var attachment = message.Attachments[0];
        Assert.Equal("application/pdf", attachment.ContentType);
        Assert.Equal(10, attachment.Size);
        Assert.True(attachment.Inline);
    }

    [Fact]
    public async Task Every_field_of_a_calendar_and_an_event_survives_the_naming_policy()
    {
        using var api = new FakeApi(
            new Reply(200, Bodies.Envelope(
                """[{"id":"c1","name":"Work","description":"team","color":"#0a0","is_owner":true}]""")),
            new Reply(200, Bodies.Envelope(
                """
                {"id":"e1","calendar_id":"c1","summary":"Standup","description":"daily",
                 "location":"room 2","starts_at":"2026-01-01T09:00:00Z","ends_at":"2026-01-01T09:15:00Z",
                 "all_day":false,"status":"CONFIRMED",
                 "attendees":[{"email":"a@b.example","name":"A","role":"CHAIR","status":"ACCEPTED"}],
                 "recurrence":["RRULE:FREQ=DAILY"]}
                """)));

        var client = api.Client();

        var calendar = (await client.Calendar.ListCalendarsAsync())[0];
        Assert.Equal("team", calendar.Description);
        Assert.Equal("#0a0", calendar.Color);
        Assert.True(calendar.IsOwner);

        var found = (await client.Calendar.GetEventAsync("c1", "e1"))!;
        Assert.Equal("c1", found.CalendarId);
        Assert.Equal("daily", found.Description);
        Assert.Equal("room 2", found.Location);
        Assert.Equal("2026-01-01T09:00:00Z", found.StartsAt);
        Assert.Equal("2026-01-01T09:15:00Z", found.EndsAt);
        Assert.False(found.AllDay);
        Assert.Equal("CONFIRMED", found.Status);
        Assert.Equal("ACCEPTED", found.Attendees![0].Status);
        Assert.Equal("A", found.Attendees[0].Name);
        Assert.Equal(["RRULE:FREQ=DAILY"], found.Recurrence);
    }

    [Fact]
    public async Task A_body_that_is_literally_null_is_not_a_failure()
    {
        using var api = new FakeApi(new Reply(200, "null"));

        var folders = await api.Client().Mail.ListFoldersAsync();
        Assert.Empty(folders);
    }

    [Fact]
    public async Task A_retry_after_header_that_is_present_but_empty()
    {
        // A proxy that sets the header and then has nothing to put in it. The
        // backoff is the answer, not a crash and not a zero-second wait.
        using var api = new FakeApi(
            new Reply(429, Bodies.Refusal("rate_limited"), new Dictionary<string, string> { ["Retry-After"] = "  " }),
            new Reply(200, Bodies.Envelope("[]")));

        await api.Client().Mail.ListFoldersAsync();

        Assert.True(Assert.Single(api.Slept) > TimeSpan.Zero);
    }

    [Fact]
    public async Task Details_that_are_not_what_details_should_be()
    {
        // An array carrying something other than objects, and objects missing
        // the fields a detail is supposed to have. Neither should stop the
        // caller from seeing the code and the message.
        using var api = new FakeApi(new Reply(400, Bodies.Refusal(
            "validation_failed", "no", details: """["a string", 7, {}, {"field":"to"}]""")));

        var error = await Assert.ThrowsAsync<VeruApiException>(() => api.Client().Mail.ListFoldersAsync());

        Assert.Equal("validation_failed", error.Code);
        Assert.Equal(2, error.Details.Count);
        Assert.Equal("", error.Details[0].Field);
        Assert.Equal("to", error.Details[1].Field);
        Assert.Equal("", error.Details[1].Message);
    }

    [Fact]
    public async Task Details_that_are_not_an_array_at_all()
    {
        using var api = new FakeApi(new Reply(400, """{"error":{"code":"x","message":"no","details":"oops"}}"""));

        var error = await Assert.ThrowsAsync<VeruApiException>(() => api.Client().Mail.ListFoldersAsync());
        Assert.Empty(error.Details);
    }

    [Fact]
    public void A_value_that_renders_to_nothing_is_left_out()
    {
        // Defensive: nothing in this client passes such a value today, and a
        // query parameter with no value would be a request for something.
        var rendered = VeruApiClient.QueryString(new Dictionary<string, object?>
        {
            ["blank"] = new Nothing(),
            ["kept"] = 7,
        });

        Assert.Equal("kept=7", rendered);
    }

    [Fact]
    public async Task Paginating_without_a_filter_is_allowed()
    {
        // The public paginate is how a caller reaches a listing this client has
        // not wrapped, and such a listing may take no parameters at all.
        using var api = new FakeApi(new Reply(200, Bodies.Envelope("""[{"id":"f1","name":"INBOX"}]""")));

        var names = new List<string>();
        await foreach (var folder in api.Client().PaginateAsync<Folder>("/v1/folders"))
        {
            names.Add(folder.Name);
        }

        Assert.Equal(["INBOX"], names);
        Assert.Equal("GET /v1/folders", api.Last.Route);
    }

    private sealed class Nothing
    {
        public override string ToString() => "";
    }
}
