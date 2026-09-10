namespace VeruSuite.Api;

/// <summary>Uploaded files, their folders, sharing, and resumable upload.</summary>
/// <remarks>
/// The folders are file-folders rather than folders because /v1/folders is
/// mail's. Two products, one word, and the API spells the less obvious one out
/// rather than leaving a caller to guess which listing they are reading.
/// </remarks>
public sealed class FilesClient
{
    private readonly VeruApiClient _api;

    internal FilesClient(VeruApiClient api) => _api = api;

    // ------------------------------------------------------------ folders

    /// <summary>The drive's folders, or its trash.</summary>
    /// <remarks>
    /// A flat list with parent ids rather than a nested tree, so a caller
    /// builds whichever shape it needs rather than this API deciding the depth.
    /// </remarks>
    public async Task<IReadOnlyList<FileFolder>> ListFoldersAsync(
        bool trashed = false,
        CancellationToken cancellationToken = default)
    {
        var query = trashed
            ? new[] { new KeyValuePair<string, object?>("trashed", "true") }
            : null;

        var (data, _) = await _api.SendAsync<List<FileFolder>>(
            HttpMethod.Get, "/v1/file-folders", query, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return data ?? new List<FileFolder>();
    }

    /// <summary>Makes a folder, at the root unless ParentId says otherwise.</summary>
    public async Task<FileFolder> CreateFolderAsync(
        FileFolder folder,
        CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<FileFolder>(
            HttpMethod.Post, "/v1/file-folders", body: folder, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return data ?? new FileFolder();
    }

    /// <summary>Renames a folder, moves it, or both in that order.</summary>
    /// <remarks>
    /// <paramref name="moveToRoot"/> is separate from <paramref name="parentId"/>
    /// because an empty parent and an absent one are the same thing once they
    /// have been through JSON, and "leave it where it is" and "move it to the
    /// top" are not the same instruction.
    /// </remarks>
    public async Task<FileFolder> UpdateFolderAsync(
        string id,
        string? name = null,
        string? parentId = null,
        bool moveToRoot = false,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>();
        if (name is not null) body["name"] = name;
        if (moveToRoot) body["parent_id"] = "";
        else if (parentId is not null) body["parent_id"] = parentId;

        var (data, _) = await _api.SendAsync<FileFolder>(
            HttpMethod.Patch, "/v1/file-folders/" + Uri.EscapeDataString(id), body: body,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return data ?? new FileFolder();
    }

    /// <summary>Moves a folder to the trash.</summary>
    public Task DeleteFolderAsync(string id, CancellationToken cancellationToken = default) =>
        _api.SendAsync<object>(
            HttpMethod.Delete, "/v1/file-folders/" + Uri.EscapeDataString(id),
            cancellationToken: cancellationToken);

    // -------------------------------------------------------------- files

    /// <summary>One page of uploaded files.</summary>
    public async Task<(IReadOnlyList<VeruFile> Files, Meta Meta)> ListFilesAsync(
        ListFilesQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var (data, meta) = await _api.SendAsync<List<VeruFile>>(
            HttpMethod.Get, "/v1/files", FileQuery(query), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return (data ?? new List<VeruFile>(), meta);
    }

    /// <summary>Every file matching the filter, following the cursor.</summary>
    public IAsyncEnumerable<VeruFile> FilesAsync(
        ListFilesQuery? query = null,
        CancellationToken cancellationToken = default) =>
        _api.PaginateAsync<VeruFile>("/v1/files", FileQuery(query), cancellationToken);

    /// <summary>One file's metadata.</summary>
    public async Task<VeruFile> GetFileAsync(string id, CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<VeruFile>(
            HttpMethod.Get, "/v1/files/" + Uri.EscapeDataString(id),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return data ?? new VeruFile();
    }

    /// <summary>A file's bytes and its content type.</summary>
    /// <remarks>
    /// <paramref name="range"/> is an HTTP range such as
    /// <c>bytes=0-1048575</c>, or null for the whole file. Worth using for
    /// anything large: the response is proxied and bounded, so a big file is
    /// fetched in parts rather than in one call that cannot finish.
    /// </remarks>
    public Task<(byte[] Bytes, string ContentType)> DownloadAsync(
        string id,
        string? range = null,
        CancellationToken cancellationToken = default) =>
        _api.DownloadAsync("/v1/files/" + Uri.EscapeDataString(id) + "/content", range, cancellationToken);

    /// <summary>Renames a file, moves it, or both.</summary>
    public async Task<VeruFile> UpdateFileAsync(
        string id,
        string? name = null,
        string? folderId = null,
        bool moveToRoot = false,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>();
        if (name is not null) body["name"] = name;
        if (moveToRoot) body["folder_id"] = "";
        else if (folderId is not null) body["folder_id"] = folderId;

        var (data, _) = await _api.SendAsync<VeruFile>(
            HttpMethod.Patch, "/v1/files/" + Uri.EscapeDataString(id), body: body,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return data ?? new VeruFile();
    }

    /// <summary>Moves a file to the trash. Nothing here frees the bytes.</summary>
    public Task DeleteFileAsync(string id, CancellationToken cancellationToken = default) =>
        _api.SendAsync<object>(
            HttpMethod.Delete, "/v1/files/" + Uri.EscapeDataString(id),
            cancellationToken: cancellationToken);

    // ------------------------------------------------------------ uploads

    /// <summary>Opens a resumable session and answers the part size to slice by.</summary>
    /// <remarks>
    /// The way to upload anything large: a single request is capped at the edge
    /// and cut at 100 seconds, so past a certain size one call cannot succeed
    /// however patient the caller is.
    /// </remarks>
    public async Task<UploadSession> StartUploadAsync(
        NewUpload upload,
        CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<UploadSession>(
            HttpMethod.Post, "/v1/files/uploads", body: upload, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return data ?? new UploadSession();
    }

    /// <summary>Sends one part's bytes.</summary>
    /// <remarks>
    /// Parts count from 1, and re-sending a number overwrites it: a part whose
    /// response was lost is simply sent again rather than restarting the
    /// upload.
    /// </remarks>
    public async Task<UploadedPart> UploadPartAsync(
        string sessionId,
        int part,
        byte[] bytes,
        CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<UploadedPart>(
            HttpMethod.Put,
            "/v1/files/uploads/" + Uri.EscapeDataString(sessionId) + "/parts/" + part,
            rawBody: bytes,
            contentType: "application/octet-stream",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return data ?? new UploadedPart();
    }

    /// <summary>Which parts have landed.</summary>
    /// <remarks>
    /// The point of a resumable upload: after an interruption, send only what
    /// is missing. A read, so it needs files.read where the rest of the session
    /// needs files.write.
    /// </remarks>
    public async Task<UploadSession> UploadStatusAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<UploadSession>(
            HttpMethod.Get, "/v1/files/uploads/" + Uri.EscapeDataString(sessionId),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return data ?? new UploadSession();
    }

    /// <summary>Assembles the parts into a file.</summary>
    public async Task<VeruFile> CompleteUploadAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<VeruFile>(
            HttpMethod.Post, "/v1/files/uploads/" + Uri.EscapeDataString(sessionId) + "/complete",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return data ?? new VeruFile();
    }

    /// <summary>Abandons a session and discards its parts.</summary>
    /// <remarks>
    /// Worth calling when you give up: the parts already stored count against
    /// the workspace until the session is abandoned.
    /// </remarks>
    public Task AbortUploadAsync(string sessionId, CancellationToken cancellationToken = default) =>
        _api.SendAsync<object>(
            HttpMethod.Delete, "/v1/files/uploads/" + Uri.EscapeDataString(sessionId),
            cancellationToken: cancellationToken);

    // ------------------------------------------------------------ sharing

    /// <summary>Who a file or document is shared with.</summary>
    /// <remarks>
    /// Needs manage access on the thing itself, not only the scope: who
    /// something is shared with is not something a viewer may read.
    /// </remarks>
    public async Task<IReadOnlyList<Permission>> ListPermissionsAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<List<Permission>>(
            HttpMethod.Get, "/v1/files/" + Uri.EscapeDataString(id) + "/permissions",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return data ?? new List<Permission>();
    }

    /// <summary>Grants somebody access.</summary>
    /// <remarks>
    /// A person is named by their workspace user id rather than their email.
    /// Needs files.share, which is separate from files.write on purpose:
    /// organising a drive and exposing it are different risks.
    /// </remarks>
    public async Task<Permission> ShareAsync(
        string id,
        Permission permission,
        CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<Permission>(
            HttpMethod.Post, "/v1/files/" + Uri.EscapeDataString(id) + "/permissions",
            body: permission, cancellationToken: cancellationToken).ConfigureAwait(false);
        return data ?? new Permission();
    }

    /// <summary>Revokes one grant.</summary>
    /// <remarks>
    /// Takes the permission's id from the listing, not the person's: one person
    /// can hold access through more than one grant.
    /// </remarks>
    public Task UnshareAsync(
        string id,
        string permissionId,
        CancellationToken cancellationToken = default) =>
        _api.SendAsync<object>(
            HttpMethod.Delete,
            "/v1/files/" + Uri.EscapeDataString(id) + "/permissions/" + Uri.EscapeDataString(permissionId),
            cancellationToken: cancellationToken);

    private static List<KeyValuePair<string, object?>>? FileQuery(ListFilesQuery? query)
    {
        if (query is null) return null;

        var q = new List<KeyValuePair<string, object?>>();
        if (!string.IsNullOrEmpty(query.FolderId)) q.Add(new("folder_id", query.FolderId));
        if (!string.IsNullOrEmpty(query.Q)) q.Add(new("q", query.Q));
        if (query.Trashed) q.Add(new("trashed", "true"));
        if (query.Limit > 0) q.Add(new("limit", query.Limit));
        if (!string.IsNullOrEmpty(query.Cursor)) q.Add(new("cursor", query.Cursor));
        return q.Count == 0 ? null : q;
    }
}
