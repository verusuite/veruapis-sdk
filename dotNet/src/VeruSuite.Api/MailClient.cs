namespace VeruSuite.Api;

/// <summary>Folders, messages, drafts and sending.</summary>
public sealed class MailClient
{
    private readonly VeruApiClient _api;

    internal MailClient(VeruApiClient api) => _api = api;

    /// <summary>
    /// Every folder, with its message and unread counts.
    /// </summary>
    /// <remarks>
    /// Not paged: a mailbox has tens of folders, so paging would mean writing a
    /// loop that never runs twice.
    /// </remarks>
    public async Task<IReadOnlyList<Folder>> ListFoldersAsync(CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<List<Folder>>(
            HttpMethod.Get, "/v1/folders", cancellationToken: cancellationToken).ConfigureAwait(false);
        return data ?? new List<Folder>();
    }

    /// <summary>
    /// One page of messages, newest first.
    /// </summary>
    /// <remarks>
    /// At least one filter is required. Prefer <see cref="MessagesAsync"/>,
    /// which walks the pages.
    /// </remarks>
    public async Task<(IReadOnlyList<MessageSummary> Messages, Meta Meta)> ListMessagesAsync(
        ListMessagesQuery query,
        CancellationToken cancellationToken = default)
    {
        var (data, meta) = await _api.SendAsync<List<MessageSummary>>(
            HttpMethod.Get, "/v1/messages", MessageQuery(query), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return (data ?? new List<MessageSummary>(), meta);
    }

    /// <summary>
    /// Every message matching the filter, following the cursor.
    /// </summary>
    /// <example>
    /// <code>
    /// await foreach (var message in api.Mail.MessagesAsync(new() { FolderId = [inbox] }))
    ///     Console.WriteLine(message.Subject);
    /// </code>
    /// </example>
    public IAsyncEnumerable<MessageSummary> MessagesAsync(
        ListMessagesQuery query,
        CancellationToken cancellationToken = default) =>
        _api.PaginateAsync<MessageSummary>("/v1/messages", MessageQuery(query), cancellationToken);

    /// <summary>One message, with its headers and body decoded.</summary>
    public async Task<Message?> GetMessageAsync(string messageId, CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<Message>(
            HttpMethod.Get, "/v1/messages/" + VeruApiClient.Escape(messageId), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return data;
    }

    /// <summary>
    /// A message's attachment metadata. Bytes are a separate call, so listing
    /// never pulls a large file through the response.
    /// </summary>
    public async Task<IReadOnlyList<Attachment>> ListAttachmentsAsync(
        string messageId,
        CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<List<Attachment>>(
            HttpMethod.Get,
            "/v1/messages/" + VeruApiClient.Escape(messageId) + "/attachments",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return data ?? new List<Attachment>();
    }

    /// <summary>One page of drafts.</summary>
    public async Task<(IReadOnlyList<MessageSummary> Drafts, Meta Meta)> ListDraftsAsync(
        int? limit = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var (data, meta) = await _api.SendAsync<List<MessageSummary>>(
            HttpMethod.Get,
            "/v1/drafts",
            new Dictionary<string, object?> { ["limit"] = limit, ["cursor"] = cursor },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return (data ?? new List<MessageSummary>(), meta);
    }

    /// <summary>
    /// Sends a message as the key's owner.
    /// </summary>
    /// <remarks>
    /// The API answers 202: the message is queued and archived in Sent, and
    /// delivery happens afterwards. That is not a promise it arrived, and a
    /// failure comes back later as a bounce rather than as an error here.
    /// <para>
    /// An Idempotency-Key is generated unless one is supplied, so a retry
    /// cannot send the same message twice.
    /// </para>
    /// </remarks>
    public async Task<Sent?> SendAsync(
        SendMessage message,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<Sent>(
            HttpMethod.Post,
            "/v1/messages/send",
            body: message,
            idempotencyKey: idempotencyKey,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return data;
    }

    private static Dictionary<string, object?> MessageQuery(ListMessagesQuery query) => new()
    {
        ["folder_id"] = query.FolderId,
        ["label"] = query.Label,
        ["flag"] = query.Flag,
        ["unread"] = query.Unread,
        ["limit"] = query.Limit,
        ["cursor"] = query.Cursor,
    };
}
