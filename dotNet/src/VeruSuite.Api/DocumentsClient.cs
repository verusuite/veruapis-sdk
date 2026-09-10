namespace VeruSuite.Api;

/// <summary>Documents and spreadsheets, their comments, and who they are shared with.</summary>
/// <remarks>
/// An uploaded file is not here. The API keeps the two apart — /v1/documents is
/// what somebody authored, /v1/files is what somebody uploaded — so a listing
/// never begins with a filter, and asking the wrong collection for an id
/// answers 404 rather than something that half fits.
/// </remarks>
public sealed class DocumentsClient
{
    private readonly VeruApiClient _api;

    internal DocumentsClient(VeruApiClient api) => _api = api;

    /// <summary>A document, as opposed to a spreadsheet or an uploaded file.</summary>
    public const string TypeDocument = "document";

    /// <summary>A spreadsheet. Created with its workbook already seeded.</summary>
    public const string TypeSpreadsheet = "spreadsheet";

    /// <summary>One page of documents and spreadsheets.</summary>
    public async Task<(IReadOnlyList<Document> Documents, Meta Meta)> ListDocumentsAsync(
        ListDocumentsQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var (data, meta) = await _api.SendAsync<List<Document>>(
            HttpMethod.Get, "/v1/documents", DocumentQuery(query), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return (data ?? new List<Document>(), meta);
    }

    /// <summary>Every document matching the filter, following the cursor.</summary>
    /// <remarks>
    /// This is how an automation finds the spreadsheet id every
    /// <see cref="SpreadsheetsClient"/> call needs, rather than having somebody
    /// paste one out of a browser.
    /// </remarks>
    public IAsyncEnumerable<Document> DocumentsAsync(
        ListDocumentsQuery? query = null,
        CancellationToken cancellationToken = default) =>
        _api.PaginateAsync<Document>("/v1/documents", DocumentQuery(query), cancellationToken);

    /// <summary>One document's metadata.</summary>
    /// <remarks>
    /// Metadata only, so this stays cheap on a large workbook. Contents are
    /// read through <see cref="SpreadsheetsClient"/>.
    /// </remarks>
    public async Task<Document> GetDocumentAsync(string id, CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<Document>(
            HttpMethod.Get, "/v1/documents/" + Uri.EscapeDataString(id),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return data ?? new Document();
    }

    /// <summary>Creates a document or a spreadsheet.</summary>
    /// <remarks>
    /// A new spreadsheet arrives with its workbook seeded, so a range can be
    /// written into it in the very next call.
    /// </remarks>
    public async Task<Document> CreateDocumentAsync(
        Document document,
        CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<Document>(
            HttpMethod.Post, "/v1/documents", body: document, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return data ?? new Document();
    }

    /// <summary>Renames a document or changes its metadata.</summary>
    /// <remarks>
    /// Anyone with it open is told, so a title changed here appears in their
    /// tab without a reload. Metadata replaces the stored object rather than
    /// merging into it.
    /// </remarks>
    public async Task<Document> UpdateDocumentAsync(
        string id,
        Document document,
        CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<Document>(
            HttpMethod.Patch, "/v1/documents/" + Uri.EscapeDataString(id), body: document,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return data ?? new Document();
    }

    /// <summary>Moves a document to the trash.</summary>
    /// <remarks>
    /// Reversible with <see cref="RestoreDocumentAsync"/>. A permanent delete
    /// is not exposed at all, so a job retrying a failed batch cannot destroy
    /// somebody's work.
    /// </remarks>
    public Task DeleteDocumentAsync(string id, CancellationToken cancellationToken = default) =>
        _api.SendAsync<object>(
            HttpMethod.Delete, "/v1/documents/" + Uri.EscapeDataString(id),
            cancellationToken: cancellationToken);

    /// <summary>Takes a document back out of the trash.</summary>
    /// <remarks>
    /// Restoring one that was never trashed changes nothing and still answers
    /// with it, so a retry is safe.
    /// </remarks>
    public async Task<Document> RestoreDocumentAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<Document>(
            HttpMethod.Post, "/v1/documents/" + Uri.EscapeDataString(id) + "/restore",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return data ?? new Document();
    }

    /// <summary>Copies a document with its contents.</summary>
    /// <remarks>
    /// Both fields are optional: with neither, the copy lands in the root as
    /// "Copy of" the original. The copy belongs to the key's owner.
    /// </remarks>
    public async Task<Document> CopyDocumentAsync(
        string id,
        string? title = null,
        string? folderId = null,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>();
        if (title is not null) body["title"] = title;
        if (folderId is not null) body["folder_id"] = folderId;

        var (data, _) = await _api.SendAsync<Document>(
            HttpMethod.Post, "/v1/documents/" + Uri.EscapeDataString(id) + "/copy", body: body,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return data ?? new Document();
    }

    /// <summary>The comment threads on a document.</summary>
    /// <remarks>
    /// Resolved threads are included unless you say otherwise, which is the
    /// opposite of the editor's sidebar: an integration auditing a document
    /// wants the whole history.
    /// </remarks>
    public async Task<IReadOnlyList<Comment>> ListCommentsAsync(
        string documentId,
        bool includeResolved = true,
        CancellationToken cancellationToken = default)
    {
        var query = includeResolved
            ? null
            : new[] { new KeyValuePair<string, object?>("include_resolved", "false") };

        var (data, _) = await _api.SendAsync<List<Comment>>(
            HttpMethod.Get, "/v1/documents/" + Uri.EscapeDataString(documentId) + "/comments", query,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return data ?? new List<Comment>();
    }

    /// <summary>Posts a comment, or a reply when ParentId is set.</summary>
    public async Task<Comment> CreateCommentAsync(
        string documentId,
        Comment comment,
        CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<Comment>(
            HttpMethod.Post, "/v1/documents/" + Uri.EscapeDataString(documentId) + "/comments",
            body: comment, cancellationToken: cancellationToken).ConfigureAwait(false);
        return data ?? new Comment();
    }

    /// <summary>Edits a comment's text or resolves the thread.</summary>
    /// <remarks>
    /// The two are not the same permission: anyone who may comment can resolve
    /// or reopen a thread, while editing the words is the author's alone.
    /// </remarks>
    public async Task<Comment> UpdateCommentAsync(
        string commentId,
        Comment comment,
        CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<Comment>(
            HttpMethod.Patch, "/v1/comments/" + Uri.EscapeDataString(commentId), body: comment,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return data ?? new Comment();
    }

    /// <summary>Removes a comment. Only its author may.</summary>
    public Task DeleteCommentAsync(string commentId, CancellationToken cancellationToken = default) =>
        _api.SendAsync<object>(
            HttpMethod.Delete, "/v1/comments/" + Uri.EscapeDataString(commentId),
            cancellationToken: cancellationToken);

    private static List<KeyValuePair<string, object?>>? DocumentQuery(ListDocumentsQuery? query)
    {
        if (query is null) return null;

        var q = new List<KeyValuePair<string, object?>>();
        if (!string.IsNullOrEmpty(query.Type)) q.Add(new("type", query.Type));
        if (!string.IsNullOrEmpty(query.FolderId)) q.Add(new("folder_id", query.FolderId));
        if (!string.IsNullOrEmpty(query.Q)) q.Add(new("q", query.Q));
        if (query.Trashed) q.Add(new("trashed", "true"));
        if (query.Limit > 0) q.Add(new("limit", query.Limit));
        if (!string.IsNullOrEmpty(query.Cursor)) q.Add(new("cursor", query.Cursor));
        return q.Count == 0 ? null : q;
    }
}
