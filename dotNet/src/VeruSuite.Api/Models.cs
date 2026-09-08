using System.Text.Json.Serialization;

namespace VeruSuite.Api;

// The shapes this API works in.
//
// Written by hand, not generated. A generator produces types you reach through
// rather than types you use, and these are short enough to read. The cost of
// writing them is drift, so it is paid for: SpecTests compares every route this
// client calls against the API's own description and fails when the two
// disagree.
//
// Records with init properties, so a response is immutable once it arrives and
// a request reads as one expression. Property names are C#; the snake_case the
// wire uses is handled by the serializer's naming policy, in one place, rather
// than by an attribute on every property.
//
// Optional fields on a request are nullable. Null means "not set" and is left
// out of the body entirely, so a partial update stays partial. That is why a
// bool that may be sent is bool? rather than bool: it lets a caller turn
// something off, which an unset false cannot express. See DECISIONS.md.

/// <summary>What accompanies every response.</summary>
public sealed record Meta
{
    /// <summary>Identifies this call. Quote it when reporting a problem.</summary>
    public string RequestId { get; init; } = "";

    public string Timestamp { get; init; } = "";

    /// <summary>Present on a list that has another page.</summary>
    public string? NextCursor { get; init; }
}

/// <summary>A person on a message.</summary>
public sealed record Address
{
    public string Email { get; init; } = "";
    public string? Name { get; init; }
}

// ------------------------------------------------------------------- mail

/// <summary>One mail folder with its counts.</summary>
public sealed record Folder
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public int MessageCount { get; init; }
    public int UnreadCount { get; init; }
}

/// <summary>The states a message carries.</summary>
public sealed record MessageFlags
{
    public bool Read { get; init; }
    public bool Starred { get; init; }
    public bool Answered { get; init; }
    public bool Draft { get; init; }
    public bool Deleted { get; init; }
}

/// <summary>
/// A message as a listing returns it. No recipients and no body: fetch the
/// message for those. The summary is small on purpose, because a folder listing
/// carrying every body would pull a whole mailbox through one response.
/// </summary>
public sealed record MessageSummary
{
    public string Id { get; init; } = "";
    public string FolderId { get; init; } = "";
    public string ThreadId { get; init; } = "";
    public string MessageId { get; init; } = "";
    public string Subject { get; init; } = "";
    public string From { get; init; } = "";
    public string Date { get; init; } = "";
    public int Size { get; init; }
    public MessageFlags Flags { get; init; } = new();
    public IReadOnlyList<string> Labels { get; init; } = Array.Empty<string>();
}

/// <summary>
/// One part of a message. Bytes are a separate call, so reading a message never
/// drags a large file through the response.
/// </summary>
public sealed record Attachment
{
    public string PartId { get; init; } = "";
    public string Filename { get; init; } = "";
    public string ContentType { get; init; } = "";
    public int Size { get; init; }

    /// <summary>True for an image the HTML body displays rather than a file to download.</summary>
    public bool Inline { get; init; }
}

/// <summary>
/// One message, fetched. MIME traversal, transfer decoding and header decoding
/// are already done.
/// </summary>
public sealed record Message
{
    public string Id { get; init; } = "";
    public string MessageId { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Date { get; init; } = "";
    public Address? From { get; init; }
    public IReadOnlyList<Address> To { get; init; } = Array.Empty<Address>();
    public IReadOnlyList<Address> Cc { get; init; } = Array.Empty<Address>();
    public string? Text { get; init; }
    public string? Html { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public IReadOnlyList<Attachment> Attachments { get; init; } = Array.Empty<Attachment>();
}

/// <summary>A file to send.</summary>
public sealed record OutgoingAttachment
{
    public required string Filename { get; init; }
    public required string ContentType { get; init; }

    /// <summary>The file, base64 encoded. Padded or not, standard or URL-safe.</summary>
    public required string ContentBase64 { get; init; }

    public bool? Inline { get; init; }

    /// <summary>
    /// Required when <see cref="Inline"/> is true. The HTML refers to it as
    /// src="cid:the-id".
    /// </summary>
    public string? ContentId { get; init; }
}

/// <summary>What sending takes.</summary>
public sealed record SendMessage
{
    public required IReadOnlyList<string> To { get; init; }
    public string? Subject { get; init; }
    public string? Text { get; init; }
    public string? Html { get; init; }
    public IReadOnlyList<string>? Cc { get; init; }
    public IReadOnlyList<string>? Bcc { get; init; }
    public string? From { get; init; }
    public string? ReplyTo { get; init; }
    public IReadOnlyList<OutgoingAttachment>? Attachments { get; init; }
}

/// <summary>
/// What the API answers when a message is accepted. A 202, not a 200: the
/// message is queued and archived in Sent, and delivery happens afterwards.
/// That is not a promise it arrived, and this API cannot tell you that it did.
/// A failure comes back later as a bounce.
/// </summary>
public sealed record Sent
{
    public string Id { get; init; } = "";
}

// --------------------------------------------------------------- calendar

/// <summary>One calendar.</summary>
public sealed record Calendar
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string? Color { get; init; }

    /// <summary>False for a calendar somebody shared with this user.</summary>
    public bool IsOwner { get; init; }
}

/// <summary>Somebody invited.</summary>
public sealed record Attendee
{
    public required string Email { get; init; }
    public string? Name { get; init; }

    /// <summary>REQ-PARTICIPANT, OPT-PARTICIPANT, NON-PARTICIPANT or CHAIR.</summary>
    public string? Role { get; init; }

    /// <summary>NEEDS-ACTION, ACCEPTED, DECLINED, TENTATIVE or DELEGATED.</summary>
    public string? Status { get; init; }
}

/// <summary>
/// One occurrence. Times are UTC. The offset a calendar application shows is
/// that application's timezone, so anything scheduling by wall-clock time needs
/// the user's zone from elsewhere.
/// </summary>
public sealed record Event
{
    public string? Id { get; init; }
    public string? CalendarId { get; init; }
    public string? Summary { get; init; }
    public string? Description { get; init; }
    public string? Location { get; init; }
    public string? StartsAt { get; init; }
    public string? EndsAt { get; init; }

    /// <summary>
    /// Nullable so that false can be sent. An unset bool and a deliberate false
    /// are different requests on a partial update.
    /// </summary>
    public bool? AllDay { get; init; }

    public string? Status { get; init; }
    public IReadOnlyList<Attendee>? Attendees { get; init; }
    public IReadOnlyList<string>? Recurrence { get; init; }
}

/// <summary>A window somebody is busy in.</summary>
public sealed record BusyPeriod
{
    public string Start { get; init; } = "";
    public string End { get; init; } = "";
}

/// <summary>
/// One person's busy windows. Free/busy answers availability without returning
/// event detail, so it needs far less access than reading everybody's calendar
/// to work the same thing out.
/// </summary>
public sealed record FreeBusy
{
    public string Email { get; init; } = "";
    public IReadOnlyList<BusyPeriod> Busy { get; init; } = Array.Empty<BusyPeriod>();
}

// ---------------------------------------------------------------- queries

/// <summary>
/// Narrows a message listing. At least one filter is required: an unfiltered
/// list would be the whole mailbox, so the API refuses it rather than quietly
/// returning the inbox.
/// </summary>
public sealed record ListMessagesQuery
{
    public IReadOnlyList<string>? FolderId { get; init; }
    public IReadOnlyList<string>? Label { get; init; }
    public string? Flag { get; init; }
    public bool? Unread { get; init; }
    public int? Limit { get; init; }

    /// <summary>
    /// From the previous page's <see cref="Meta.NextCursor"/>. Prefer the
    /// streaming methods, which carry it for you.
    /// </summary>
    public string? Cursor { get; init; }
}

/// <summary>Narrows an event listing.</summary>
public sealed record ListEventsQuery
{
    /// <summary>
    /// Required. A recurring series expands without limit, so there is no
    /// finite answer to "all events".
    /// </summary>
    public required string Start { get; init; }

    /// <summary>Required, for the same reason as <see cref="Start"/>.</summary>
    public required string End { get; init; }

    public IReadOnlyList<string>? CalendarId { get; init; }
    public int? Limit { get; init; }
    public string? Cursor { get; init; }
}

/// <summary>The shape every response arrives in.</summary>
internal sealed record Envelope<T>
{
    [JsonPropertyName("data")]
    public T? Data { get; init; }

    [JsonPropertyName("meta")]
    public Meta Meta { get; init; } = new();
}
