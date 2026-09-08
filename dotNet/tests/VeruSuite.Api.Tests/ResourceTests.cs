using System.Text.Json;
using VeruSuite.Api;
using Xunit;

namespace VeruSuite.Api.Tests;

/// <summary>Every method: the request it makes, and what it makes of the answer.</summary>
public class MailTests
{
    [Fact]
    public async Task ListFolders()
    {
        using var api = new FakeApi(new Reply(200, Bodies.Envelope(
            @"[{""id"":""f1"",""name"":""INBOX"",""message_count"":3,""unread_count"":1}]")));

        var folders = await api.Client().Mail.ListFoldersAsync();

        Assert.Equal("GET /v1/folders", api.Last.Route);
        Assert.Equal("INBOX", folders[0].Name);
        Assert.Equal(3, folders[0].MessageCount);
        Assert.Equal(1, folders[0].UnreadCount);
    }

    [Fact]
    public async Task ListMessages_sends_the_filter_and_returns_meta()
    {
        using var api = new FakeApi(new Reply(200, Bodies.Envelope(
            @"[{""id"":""m1"",""subject"":""hello"",""from"":""a@b.example"",
                ""flags"":{""read"":true},""labels"":[""work""]}]", nextCursor: "c2")));

        var (messages, meta) = await api.Client().Mail.ListMessagesAsync(new ListMessagesQuery
        {
            FolderId = ["f1", "f2"],
            Label = ["work"],
            Flag = "starred",
            Unread = true,
            Limit = 5,
        });

        var request = api.Last;
        Assert.Equal("GET /v1/messages", request.Route);
        Assert.Equal(["f1", "f2"], request.Query["folder_id"]);
        Assert.Equal(["work"], request.Query["label"]);
        Assert.Equal(["starred"], request.Query["flag"]);
        Assert.Equal(["true"], request.Query["unread"]);
        Assert.Equal(["5"], request.Query["limit"]);

        Assert.Equal("a@b.example", messages[0].From);
        Assert.True(messages[0].Flags.Read);
        Assert.False(messages[0].Flags.Starred);
        Assert.Equal(["work"], messages[0].Labels);
        Assert.Equal("c2", meta.NextCursor);
    }

    [Fact]
    public async Task GetMessage_decodes_the_nested_shapes()
    {
        using var api = new FakeApi(new Reply(200, Bodies.Envelope(
            @"{""id"":""m1"",""subject"":""hello"",
               ""from"":{""email"":""a@b.example"",""name"":""A""},
               ""to"":[{""email"":""c@d.example""}],
               ""headers"":{""X-Thing"":""1""},
               ""attachments"":[{""part_id"":""2"",""filename"":""r.pdf"",""size"":10,""inline"":false}]}")));

        var message = await api.Client().Mail.GetMessageAsync("m1");

        Assert.Equal("GET /v1/messages/m1", api.Last.Route);
        Assert.Equal("A", message!.From!.Name);
        Assert.Equal("c@d.example", message.To[0].Email);
        Assert.Equal("1", message.Headers!["X-Thing"]);
        Assert.Equal("r.pdf", message.Attachments[0].Filename);
    }

    [Fact]
    public async Task GetMessage_survives_a_field_it_has_never_heard_of()
    {
        // The API adds fields within a version. A client that failed on an
        // unfamiliar key would break every time the server learned something.
        using var api = new FakeApi(new Reply(200, Bodies.Envelope(
            @"{""id"":""m1"",""subject"":""hi"",""invented_last_week"":{""a"":1}}")));

        var message = await api.Client().Mail.GetMessageAsync("m1");
        Assert.Equal("hi", message!.Subject);
    }

    [Fact]
    public async Task ListAttachments()
    {
        using var api = new FakeApi(new Reply(200, Bodies.Envelope(
            @"[{""part_id"":""2"",""filename"":""r.pdf""}]")));

        var attachments = await api.Client().Mail.ListAttachmentsAsync("m1");

        Assert.Equal("GET /v1/messages/m1/attachments", api.Last.Route);
        Assert.Equal("2", attachments[0].PartId);
    }

    [Fact]
    public async Task ListDrafts()
    {
        using var api = new FakeApi();
        await api.Client().Mail.ListDraftsAsync(limit: 3, cursor: "c1");

        Assert.Equal("GET /v1/drafts", api.Last.Route);
        Assert.Equal(["3"], api.Last.Query["limit"]);
        Assert.Equal(["c1"], api.Last.Query["cursor"]);
    }

    [Fact]
    public async Task Send_leaves_out_what_was_not_set()
    {
        // Sending every default would make a partial update a full replacement,
        // and would fill the wire with nulls on every call.
        using var api = new FakeApi(new Reply(202, Bodies.Envelope(@"{""id"":""m9""}")));

        var sent = await api.Client().Mail.SendAsync(new SendMessage
        {
            To = ["a@b.example"],
            Subject = "one",
        });

        Assert.Equal("POST /v1/messages/send", api.Last.Route);
        Assert.Equal("m9", sent!.Id);

        var body = api.Last.Json;
        Assert.Equal(2, body.EnumerateObject().Count());
        Assert.Equal("one", body.GetProperty("subject").GetString());
    }

    [Fact]
    public async Task Send_carries_attachments()
    {
        using var api = new FakeApi();

        await api.Client().Mail.SendAsync(new SendMessage
        {
            To = ["a@b.example"],
            Attachments =
            [
                new OutgoingAttachment
                {
                    Filename = "r.pdf",
                    ContentType = "application/pdf",
                    ContentBase64 = "AA==",
                },
            ],
        });

        var attachment = api.Last.Json.GetProperty("attachments")[0];
        Assert.Equal("r.pdf", attachment.GetProperty("filename").GetString());
        Assert.False(attachment.TryGetProperty("content_id", out _));
    }
}

public class CalendarTests
{
    [Fact]
    public async Task ListCalendars()
    {
        using var api = new FakeApi(new Reply(200, Bodies.Envelope(
            @"[{""id"":""c1"",""name"":""Work"",""is_owner"":true}]")));

        var calendars = await api.Client().Calendar.ListCalendarsAsync();

        Assert.Equal("GET /v1/calendars", api.Last.Route);
        Assert.True(calendars[0].IsOwner);
    }

    [Fact]
    public async Task GetCalendar()
    {
        using var api = new FakeApi(new Reply(200, Bodies.Envelope(@"{""id"":""c1"",""name"":""Work""}")));

        var calendar = await api.Client().Calendar.GetCalendarAsync("c1");

        Assert.Equal("GET /v1/calendars/c1", api.Last.Route);
        Assert.Equal("Work", calendar!.Name);
    }

    [Fact]
    public async Task ListEvents()
    {
        using var api = new FakeApi(new Reply(200, Bodies.Envelope(
            @"[{""id"":""e1"",""summary"":""Standup"",""starts_at"":""2026-01-01T09:00:00Z"",
                ""attendees"":[{""email"":""a@b.example"",""role"":""CHAIR""}]}]")));

        var (events, _) = await api.Client().Calendar.ListEventsAsync(new ListEventsQuery
        {
            Start = "s",
            End = "e",
            CalendarId = ["c1"],
            Limit = 2,
        });

        Assert.Equal("GET /v1/events", api.Last.Route);
        Assert.Equal(["s"], api.Last.Query["start"]);
        Assert.Equal(["c1"], api.Last.Query["calendar_id"]);
        Assert.Equal("CHAIR", events[0].Attendees![0].Role);
    }

    [Fact]
    public async Task Events_walks_the_pages()
    {
        using var api = new FakeApi(
            new Reply(200, Bodies.Envelope(@"[{""id"":""e1""}]", nextCursor: "c2")),
            new Reply(200, Bodies.Envelope(@"[{""id"":""e2""}]")));

        var ids = new List<string>();
        await foreach (var item in api.Client().Calendar.EventsAsync(new ListEventsQuery { Start = "s", End = "e" }))
        {
            ids.Add(item.Id!);
        }

        Assert.Equal(["e1", "e2"], ids);
        Assert.Equal(["c2"], api.Requests[1].Query["cursor"]);
    }

    [Fact]
    public async Task GetEvent()
    {
        using var api = new FakeApi(new Reply(200, Bodies.Envelope(@"{""id"":""e1"",""summary"":""Standup""}")));

        var found = await api.Client().Calendar.GetEventAsync("c1", "e1");

        Assert.Equal("GET /v1/calendars/c1/events/e1", api.Last.Route);
        Assert.Equal("Standup", found!.Summary);
    }

    [Fact]
    public async Task CreateEvent_cannot_double_book_on_a_retry()
    {
        using var api = new FakeApi(new Reply(200, Bodies.Envelope(@"{""id"":""e1""}")));

        await api.Client().Calendar.CreateEventAsync("c1", new Event
        {
            Summary = "Standup",
            StartsAt = "2026-01-01T09:00:00Z",
            EndsAt = "2026-01-01T09:15:00Z",
            Attendees = [new Attendee { Email = "a@b.example", Role = "CHAIR" }],
        });

        Assert.Equal("POST /v1/calendars/c1/events", api.Last.Route);
        Assert.Equal("Standup", api.Last.Json.GetProperty("summary").GetString());
        Assert.NotEmpty(api.Last.Headers["idempotency-key"]);
    }

    [Fact]
    public async Task UpdateEvent_sends_only_what_changed()
    {
        using var api = new FakeApi(new Reply(200, Bodies.Envelope(@"{""id"":""e1""}")));

        await api.Client().Calendar.UpdateEventAsync("c1", "e1", new Event { Summary = "Moved" });

        Assert.Equal("PATCH /v1/calendars/c1/events/e1", api.Last.Route);
        Assert.Equal(@"{""summary"":""Moved""}", api.Last.Body);
    }

    [Fact]
    public async Task An_explicit_false_can_be_sent()
    {
        // Nullable rather than bool, so that turning something off is a
        // different request from never mentioning it. See DECISIONS.md: this is
        // the one behaviour where this client can express more than the Go and
        // Python ones.
        using var api = new FakeApi(new Reply(200, Bodies.Envelope(@"{""id"":""e1""}")));

        await api.Client().Calendar.UpdateEventAsync("c1", "e1", new Event { AllDay = false });

        Assert.Equal(@"{""all_day"":false}", api.Last.Body);
    }

    [Fact]
    public async Task DeleteEvent()
    {
        using var api = new FakeApi(new Reply(204));

        await api.Client().Calendar.DeleteEventAsync("c1", "e1");

        Assert.Equal("DELETE /v1/calendars/c1/events/e1", api.Last.Route);
        Assert.DoesNotContain("idempotency-key", api.Last.Headers.Keys);
    }

    [Fact]
    public async Task FreeBusy()
    {
        using var api = new FakeApi(new Reply(200, Bodies.Envelope(
            @"[{""email"":""a@b.example"",""busy"":[{""start"":""s"",""end"":""e""}]}]")));

        var busy = await api.Client().Calendar.FreeBusyAsync(["a@b.example"], "s", "e");

        Assert.Equal("POST /v1/freebusy", api.Last.Route);
        Assert.Equal(@"{""emails"":[""a@b.example""],""start"":""s"",""end"":""e""}", api.Last.Body);
        Assert.Equal("s", busy[0].Busy[0].Start);
    }
}

public class PaginationTests
{
    [Fact]
    public async Task The_cursor_is_followed_to_the_end()
    {
        using var api = new FakeApi(
            new Reply(200, Bodies.Envelope(@"[{""id"":""m1""}]", nextCursor: "c2")),
            new Reply(200, Bodies.Envelope(@"[{""id"":""m2""}]", nextCursor: "c3")),
            new Reply(200, Bodies.Envelope(@"[{""id"":""m3""}]")));

        var ids = new List<string>();
        await foreach (var message in api.Client().Mail.MessagesAsync(new ListMessagesQuery { FolderId = ["f1"] }))
        {
            ids.Add(message.Id);
        }

        Assert.Equal(["m1", "m2", "m3"], ids);
        Assert.Empty(api.Requests[0].Query["cursor"]);
        Assert.Equal(["c2"], api.Requests[1].Query["cursor"]);
        Assert.Equal(["c3"], api.Requests[2].Query["cursor"]);
    }

    [Fact]
    public async Task The_filter_is_carried_onto_every_page()
    {
        using var api = new FakeApi(
            new Reply(200, Bodies.Envelope(@"[{""id"":""m1""}]", nextCursor: "c2")),
            new Reply(200, Bodies.Envelope(@"[{""id"":""m2""}]")));

        await foreach (var _ in api.Client().Mail.MessagesAsync(
            new ListMessagesQuery { FolderId = ["f1"], Unread = true }))
        {
        }

        foreach (var request in api.Requests)
        {
            Assert.Equal(["f1"], request.Query["folder_id"]);
            Assert.Equal(["true"], request.Query["unread"]);
        }
    }

    [Fact]
    public async Task An_empty_page_with_a_cursor_does_not_loop_forever()
    {
        // The failure this guards against does not throw, it hangs, which is
        // the worst way for a client library to be wrong.
        using var api = new FakeApi();
        api.Default = new Reply(200, Bodies.Envelope("[]", nextCursor: "always"));

        await foreach (var _ in api.Client().Mail.MessagesAsync(new ListMessagesQuery { FolderId = ["f1"] }))
        {
        }

        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task A_caller_that_stops_early_stops_the_walk()
    {
        using var api = new FakeApi(
            new Reply(200, Bodies.Envelope(@"[{""id"":""m1""},{""id"":""m2""}]", nextCursor: "c2")),
            new Reply(200, Bodies.Envelope(@"[{""id"":""m3""}]")));

        await foreach (var message in api.Client().Mail.MessagesAsync(new ListMessagesQuery { FolderId = ["f1"] }))
        {
            Assert.Equal("m1", message.Id);
            break;
        }

        Assert.Single(api.Requests); // the second page was never asked for
    }

    [Fact]
    public async Task A_failure_part_way_through_throws()
    {
        using var api = new FakeApi(
            new Reply(200, Bodies.Envelope(@"[{""id"":""m1""}]", nextCursor: "c2")),
            new Reply(403, Bodies.Refusal("insufficient_scope")));

        var seen = new List<string>();

        await Assert.ThrowsAsync<VeruApiException>(async () =>
        {
            await foreach (var message in api.Client().Mail.MessagesAsync(new ListMessagesQuery { FolderId = ["f1"] }))
            {
                seen.Add(message.Id);
            }
        });

        Assert.Equal(["m1"], seen); // what was yielded before the failure is kept
    }
}
