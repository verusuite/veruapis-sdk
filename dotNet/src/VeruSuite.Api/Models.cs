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


// ------------------------------------------------------------- spreadsheets

/// <summary>A spreadsheet's structure: its tabs and what is on them.</summary>
public sealed class Workbook
{
    [JsonPropertyName("document_id")] public string DocumentId { get; init; } = "";
    [JsonPropertyName("sheets")] public List<Sheet> Sheets { get; init; } = new();
}

/// <summary>One tab, with the extent of what is actually on it.</summary>
public sealed class Sheet
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("index")] public int Index { get; init; }

    /// <summary>
    /// The last populated row and column, zero-based. An unbounded range such
    /// as A:A is clamped to these, not to the million rows a grid permits.
    /// </summary>
    [JsonPropertyName("max_row")] public int MaxRow { get; init; }

    /// <inheritdoc cref="MaxRow"/>
    [JsonPropertyName("max_column")] public int MaxColumn { get; init; }

    [JsonPropertyName("range")] public string Range { get; init; } = "";
    [JsonPropertyName("merges")] public List<Merge> Merges { get; init; } = new();
}

/// <summary>One merged rectangle, zero-based and inclusive at both corners.</summary>
public sealed class Merge
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("c0")] public int C0 { get; init; }
    [JsonPropertyName("r0")] public int R0 { get; init; }
    [JsonPropertyName("c1")] public int C1 { get; init; }
    [JsonPropertyName("r1")] public int R1 { get; init; }
}

/// <summary>A document's change token: poll it, or send it as a precondition.</summary>
public sealed class DocumentState
{
    [JsonPropertyName("document_id")] public string DocumentId { get; init; } = "";
    [JsonPropertyName("state")] public string State { get; init; } = "";
    [JsonPropertyName("checked_at")] public string CheckedAt { get; init; } = "";
}

/// <summary>One rectangle of cells.</summary>
/// <remarks>
/// Reads are always rectangular — an empty cell arrives as an empty string — so
/// a caller need not bounds-check each row. A cell is whatever fits: a string,
/// a number, a bool, or a formula written as a string beginning with "=".
/// </remarks>
public sealed class ValueRange
{
    [JsonPropertyName("range")] public string Range { get; init; } = "";
    [JsonPropertyName("values")] public List<List<object?>> Values { get; init; } = new();
}

/// <summary>The envelope a batch read answers with.</summary>
internal sealed class BatchValues
{
    [JsonPropertyName("value_ranges")] public List<ValueRange> ValueRanges { get; init; } = new();
}

/// <summary>What a value write reports.</summary>
public sealed class WriteResult
{
    /// <summary>Where the values landed. Set on a single-range write and on an append.</summary>
    [JsonPropertyName("range")] public string Range { get; init; } = "";

    [JsonPropertyName("updated_cells")] public int UpdatedCells { get; init; }

    /// <summary>
    /// The document's state after the write. Keep it to notice somebody else's
    /// edit, or to send as the precondition on a structural change.
    /// </summary>
    [JsonPropertyName("state")] public string State { get; init; } = "";

    /// <summary>
    /// False when the write stored nothing new — writing a cell the value it
    /// already holds. Not an error and not worth retrying: nothing was stored
    /// and nobody with the document open was told.
    /// </summary>
    [JsonPropertyName("changed")] public bool Changed { get; init; }
}

/// <summary>What a structural batch reports.</summary>
/// <remarks>
/// <see cref="Replies"/> is positional: one entry per request, in the order
/// sent, empty where an operation returns nothing.
/// </remarks>
public sealed class StructureResult
{
    [JsonPropertyName("replies")] public List<Dictionary<string, object?>> Replies { get; init; } = new();
    [JsonPropertyName("state")] public string State { get; init; } = "";
    [JsonPropertyName("changed")] public bool Changed { get; init; }
}


// ------------------------------------------------------ workspace and drive

/// <summary>The user a key acts as. A key can never do more than they can.</summary>
public sealed class Profile
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("email")] public string Email { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("role")] public string Role { get; init; } = "";
    [JsonPropertyName("workspace")] public Workspace Workspace { get; init; } = new();

    /// <summary>Empty for an account with no mailbox.</summary>
    [JsonPropertyName("mailbox_address")] public string MailboxAddress { get; init; } = "";

    [JsonPropertyName("avatar_url")] public string AvatarUrl { get; init; } = "";
}

/// <summary>The workspace a key belongs to. A key never spans workspaces.</summary>
public sealed class Workspace
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
}

/// <summary>One workspace group as a member sees it.</summary>
public sealed class Group
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";

    /// <summary>
    /// null when withheld, which happens for a group the caller cannot see
    /// into. null is not zero: it means unknown, not empty.
    /// </summary>
    [JsonPropertyName("member_count")] public int? MemberCount { get; init; }

    [JsonPropertyName("updated_at")] public string UpdatedAt { get; init; } = "";
}

/// <summary>One address book in the caller's mailbox.</summary>
public sealed class AddressBook
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("contact_count")] public int ContactCount { get; init; }
    [JsonPropertyName("created_at")] public string CreatedAt { get; init; } = "";
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; init; } = "";
}

/// <summary>One address on a contact.</summary>
public sealed class ContactEmail
{
    [JsonPropertyName("address")] public string Address { get; init; } = "";
    [JsonPropertyName("type")] public string? Type { get; init; }
}

/// <summary>One number on a contact.</summary>
public sealed class ContactPhone
{
    [JsonPropertyName("number")] public string Number { get; init; } = "";
    [JsonPropertyName("type")] public string? Type { get; init; }
}

/// <summary>One entry in an address book.</summary>
public sealed class Contact
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("address_book_id")] public string? AddressBookId { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("given_name")] public string? GivenName { get; init; }
    [JsonPropertyName("family_name")] public string? FamilyName { get; init; }
    [JsonPropertyName("emails")] public List<ContactEmail>? Emails { get; init; }
    [JsonPropertyName("phones")] public List<ContactPhone>? Phones { get; init; }
    [JsonPropertyName("organization")] public string? Organization { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("notes")] public string? Notes { get; init; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; init; }
    [JsonPropertyName("updated_at")] public string? UpdatedAt { get; init; }
}

/// <summary>Filters a contact listing.</summary>
public sealed class ListContactsQuery
{
    public string? AddressBookId { get; init; }

    /// <summary>Matches the name and every address, including secondary ones.</summary>
    public string? Q { get; init; }

    public int Limit { get; init; }
    public string? Cursor { get; init; }
}

/// <summary>A document or a spreadsheet. An uploaded file is a VeruFile.</summary>
public sealed class Document
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }

    /// <summary>"active" or "trashed". Deleting trashes; nothing here destroys.</summary>
    [JsonPropertyName("state")] public string? State { get; init; }

    /// <summary>Who owns it, which is not necessarily the key's owner.</summary>
    [JsonPropertyName("owner_id")] public string? OwnerId { get; init; }

    /// <summary>Empty for a document in the root.</summary>
    [JsonPropertyName("folder_id")] public string? FolderId { get; init; }

    /// <summary>Yours to shape. The API stores it and does not read it.</summary>
    [JsonPropertyName("metadata")] public Dictionary<string, object?>? Metadata { get; init; }

    [JsonPropertyName("created_at")] public string? CreatedAt { get; init; }
    [JsonPropertyName("updated_at")] public string? UpdatedAt { get; init; }
}

/// <summary>Filters a document listing.</summary>
public sealed class ListDocumentsQuery
{
    public string? Type { get; init; }
    public string? FolderId { get; init; }
    public string? Q { get; init; }
    public bool Trashed { get; init; }
    public int Limit { get; init; }
    public string? Cursor { get; init; }
}

/// <summary>One comment or reply on a document.</summary>
public sealed class Comment
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("document_id")] public string? DocumentId { get; init; }

    /// <summary>Set on a reply, absent on a thread's first comment.</summary>
    [JsonPropertyName("parent_id")] public string? ParentId { get; init; }

    [JsonPropertyName("author_id")] public string? AuthorId { get; init; }
    [JsonPropertyName("body")] public string? Body { get; init; }

    /// <summary>"open" or "resolved".</summary>
    [JsonPropertyName("state")] public string? State { get; init; }

    [JsonPropertyName("created_at")] public string? CreatedAt { get; init; }
    [JsonPropertyName("updated_at")] public string? UpdatedAt { get; init; }
}

/// <summary>One grant of access to a document or file.</summary>
public sealed class Permission
{
    [JsonPropertyName("id")] public string? Id { get; init; }

    /// <summary>"user" or "group"; PrincipalId is that id, not an email.</summary>
    [JsonPropertyName("principal_type")] public string? PrincipalType { get; init; }

    [JsonPropertyName("principal_id")] public string? PrincipalId { get; init; }
    [JsonPropertyName("role")] public string? Role { get; init; }
    [JsonPropertyName("created_by")] public string? CreatedBy { get; init; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; init; }
}

/// <summary>An uploaded file.</summary>
/// <remarks>
/// Named VeruFile rather than File because System.IO.File is in scope in almost
/// every program that would use this, and a client library should not make
/// somebody disambiguate their own file handling.
/// </remarks>
public sealed class VeruFile
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }

    /// <summary>The display name, which can change.</summary>
    [JsonPropertyName("title")] public string? Title { get; init; }

    /// <summary>What it was uploaded as, which does not.</summary>
    [JsonPropertyName("filename")] public string? Filename { get; init; }

    /// <summary>Sniffed from the bytes when stored, not taken from what was declared.</summary>
    [JsonPropertyName("mime_type")] public string? MimeType { get; init; }

    [JsonPropertyName("size_bytes")] public long? SizeBytes { get; init; }
    [JsonPropertyName("state")] public string? State { get; init; }
    [JsonPropertyName("owner_id")] public string? OwnerId { get; init; }
    [JsonPropertyName("folder_id")] public string? FolderId { get; init; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; init; }
    [JsonPropertyName("updated_at")] public string? UpdatedAt { get; init; }
}

/// <summary>Filters a file listing.</summary>
public sealed class ListFilesQuery
{
    public string? FolderId { get; init; }
    public string? Q { get; init; }
    public bool Trashed { get; init; }
    public int Limit { get; init; }
    public string? Cursor { get; init; }
}

/// <summary>One folder in the drive.</summary>
public sealed class FileFolder
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }

    /// <summary>Empty for a folder at the root.</summary>
    [JsonPropertyName("parent_id")] public string? ParentId { get; init; }

    [JsonPropertyName("owner_id")] public string? OwnerId { get; init; }
    [JsonPropertyName("trashed")] public bool Trashed { get; init; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; init; }
    [JsonPropertyName("updated_at")] public string? UpdatedAt { get; init; }
}

/// <summary>A file about to be sent.</summary>
public sealed class NewUpload
{
    [JsonPropertyName("filename")] public string Filename { get; init; } = "";

    /// <summary>Required: it decides the part size that comes back.</summary>
    [JsonPropertyName("size")] public long Size { get; init; }

    /// <summary>Recorded, not trusted: the stored type is sniffed from the bytes.</summary>
    [JsonPropertyName("mime_type")] public string? MimeType { get; init; }

    [JsonPropertyName("folder_id")] public string? FolderId { get; init; }
}

/// <summary>An upload in progress.</summary>
public sealed class UploadSession
{
    [JsonPropertyName("session_id")] public string SessionId { get; init; } = "";

    /// <summary>The file this will become, reserved before the bytes are all up.</summary>
    [JsonPropertyName("document_id")] public string DocumentId { get; init; } = "";

    /// <summary>Slice by exactly this, every part but the last.</summary>
    [JsonPropertyName("part_size")] public long PartSize { get; init; }

    [JsonPropertyName("total_size")] public long TotalSize { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }

    /// <summary>
    /// Which numbers have landed. Read from storage rather than from anything a
    /// client remembered, so a resume survives a restart.
    /// </summary>
    [JsonPropertyName("uploaded_parts")] public List<int>? UploadedParts { get; init; }

    [JsonPropertyName("uploaded_bytes")] public long? UploadedBytes { get; init; }
}

/// <summary>The receipt for one part.</summary>
public sealed class UploadedPart
{
    [JsonPropertyName("part")] public int Part { get; init; }
    [JsonPropertyName("etag")] public string ETag { get; init; } = "";
}
