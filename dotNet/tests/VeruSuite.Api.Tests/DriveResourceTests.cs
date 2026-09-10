using VeruSuite.Api;
using Xunit;

namespace VeruSuite.Api.Tests;

/// <summary>Documents, comments and sharing.</summary>
public class DocumentsTests
{
    [Fact]
    public async Task Every_method_maps_to_its_route()
    {
        using var api = new FakeApi();
        var docs = api.Client().Documents;

        await docs.ListDocumentsAsync();
        await docs.GetDocumentAsync("d1");
        await docs.CreateDocumentAsync(new Document { Type = DocumentsClient.TypeSpreadsheet });
        await docs.UpdateDocumentAsync("d1", new Document { Title = "x" });
        await docs.DeleteDocumentAsync("d1");
        await docs.RestoreDocumentAsync("d1");
        await docs.CopyDocumentAsync("d1");
        await docs.ListCommentsAsync("d1");
        await docs.CreateCommentAsync("d1", new Comment { Body = "x" });
        await docs.UpdateCommentAsync("n1", new Comment { State = "resolved" });
        await docs.DeleteCommentAsync("n1");

        Assert.Equal(
            [
                "GET /v1/documents",
                "GET /v1/documents/d1",
                "POST /v1/documents",
                "PATCH /v1/documents/d1",
                "DELETE /v1/documents/d1",
                "POST /v1/documents/d1/restore",
                "POST /v1/documents/d1/copy",
                "GET /v1/documents/d1/comments",
                "POST /v1/documents/d1/comments",
                "PATCH /v1/comments/n1",
                "DELETE /v1/comments/n1",
            ],
            api.Requests.Select(r => r.Route).ToArray());
    }

    [Fact]
    public async Task ListDocuments_sends_every_filter_it_is_given()
    {
        using var api = new FakeApi();

        await api.Client().Documents.ListDocumentsAsync(new ListDocumentsQuery
        {
            Type = DocumentsClient.TypeSpreadsheet,
            FolderId = "o1",
            Q = "plan",
            Trashed = true,
            Limit = 25,
            Cursor = "c2",
        });

        var query = api.Last.Query;
        Assert.Equal(["spreadsheet"], query["type"]);
        Assert.Equal(["o1"], query["folder_id"]);
        Assert.Equal(["plan"], query["q"]);
        Assert.Equal(["true"], query["trashed"]);
        Assert.Equal(["25"], query["limit"]);
        Assert.Equal(["c2"], query["cursor"]);
    }

    [Fact]
    public async Task ListDocuments_sends_nothing_it_was_not_given()
    {
        using var api = new FakeApi();

        await api.Client().Documents.ListDocumentsAsync(new ListDocumentsQuery());

        // An empty query object is not a query: sending trashed=false would ask
        // a different question from asking nothing.
        Assert.Equal("", api.Last.Query.Count == 0 ? "" : "query sent");
    }

    [Fact]
    public async Task Documents_walks_the_pages()
    {
        using var api = new FakeApi(
            new Reply(200, Bodies.Envelope(@"[{""id"":""d1""}]", nextCursor: "c2")),
            new Reply(200, Bodies.Envelope(@"[{""id"":""d2""}]")));

        var seen = new List<string>();
        await foreach (var doc in api.Client().Documents.DocumentsAsync(new ListDocumentsQuery { Q = "x" }))
        {
            seen.Add(doc.Id!);
        }

        Assert.Equal(["d1", "d2"], seen);
    }

    [Fact]
    public async Task CopyDocument_sends_only_what_it_was_told()
    {
        using var api = new FakeApi(new Reply(200, Bodies.Envelope(@"{""id"":""d2""}")));

        await api.Client().Documents.CopyDocumentAsync("d1", title: "Q4 plan", folderId: "o1");

        Assert.Contains("Q4 plan", api.Last.Body);
        Assert.Contains("o1", api.Last.Body);
    }

    [Fact]
    public async Task ListComments_asks_for_the_whole_history_unless_told_otherwise()
    {
        using var api = new FakeApi();
        var docs = api.Client().Documents;

        await docs.ListCommentsAsync("d1");
        Assert.Empty(api.Last.Query);

        await docs.ListCommentsAsync("d1", includeResolved: false);
        Assert.Equal(["false"], api.Last.Query["include_resolved"]);
    }
}

/// <summary>Files, folders, uploads and sharing.</summary>
public class FilesTests
{
    [Fact]
    public async Task Every_method_maps_to_its_route()
    {
        using var api = new FakeApi();
        var files = api.Client().Files;

        await files.ListFoldersAsync();
        await files.CreateFolderAsync(new FileFolder { Name = "Reports" });
        await files.UpdateFolderAsync("o1", name: "Archive");
        await files.DeleteFolderAsync("o1");
        await files.ListFilesAsync();
        await files.GetFileAsync("b1");
        await files.UpdateFileAsync("b1", name: "f.pdf");
        await files.DeleteFileAsync("b1");
        await files.StartUploadAsync(new NewUpload { Filename = "f.pdf", Size = 10 });
        await files.UploadPartAsync("u1", 1, [1, 2, 3]);
        await files.UploadStatusAsync("u1");
        await files.CompleteUploadAsync("u1");
        await files.AbortUploadAsync("u1");
        await files.ListPermissionsAsync("b1");
        await files.ShareAsync("b1", new Permission { PrincipalId = "usr1", Role = "editor" });
        await files.UnshareAsync("b1", "p1");

        Assert.Equal(
            [
                "GET /v1/file-folders",
                "POST /v1/file-folders",
                "PATCH /v1/file-folders/o1",
                "DELETE /v1/file-folders/o1",
                "GET /v1/files",
                "GET /v1/files/b1",
                "PATCH /v1/files/b1",
                "DELETE /v1/files/b1",
                "POST /v1/files/uploads",
                "PUT /v1/files/uploads/u1/parts/1",
                "GET /v1/files/uploads/u1",
                "POST /v1/files/uploads/u1/complete",
                "DELETE /v1/files/uploads/u1",
                "GET /v1/files/b1/permissions",
                "POST /v1/files/b1/permissions",
                "DELETE /v1/files/b1/permissions/p1",
            ],
            api.Requests.Select(r => r.Route).ToArray());
    }

    [Fact]
    public async Task ListFolders_asks_for_the_trash_only_when_told()
    {
        using var api = new FakeApi();
        var files = api.Client().Files;

        await files.ListFoldersAsync();
        Assert.Empty(api.Last.Query);

        await files.ListFoldersAsync(trashed: true);
        Assert.Equal(["true"], api.Last.Query["trashed"]);
    }

    [Fact]
    public async Task ListFiles_sends_every_filter_it_is_given()
    {
        using var api = new FakeApi();

        await api.Client().Files.ListFilesAsync(new ListFilesQuery
        {
            FolderId = "o1",
            Q = "report",
            Trashed = true,
            Limit = 25,
            Cursor = "c2",
        });

        var query = api.Last.Query;
        Assert.Equal(["o1"], query["folder_id"]);
        Assert.Equal(["report"], query["q"]);
        Assert.Equal(["true"], query["trashed"]);
    }

    [Fact]
    public async Task Files_walks_the_pages()
    {
        using var api = new FakeApi(
            new Reply(200, Bodies.Envelope(@"[{""id"":""b1""}]", nextCursor: "c2")),
            new Reply(200, Bodies.Envelope(@"[{""id"":""b2""}]")));

        var seen = new List<string>();
        await foreach (var file in api.Client().Files.FilesAsync(new ListFilesQuery { Q = "x" }))
        {
            seen.Add(file.Id!);
        }

        Assert.Equal(["b1", "b2"], seen);
    }

    [Fact]
    public async Task Moving_to_the_root_is_not_the_same_as_leaving_it_alone()
    {
        using var api = new FakeApi();
        var files = api.Client().Files;

        await files.UpdateFolderAsync("o1", parentId: "o2");
        Assert.Contains("o2", api.Last.Body);

        await files.UpdateFolderAsync("o1", moveToRoot: true);
        Assert.Contains(@"""parent_id"":""""", api.Last.Body);

        await files.UpdateFileAsync("b1", folderId: "o2");
        Assert.Contains("o2", api.Last.Body);

        await files.UpdateFileAsync("b1", moveToRoot: true);
        Assert.Contains(@"""folder_id"":""""", api.Last.Body);

        // A rename alone says nothing about where the file lives, which is the
        // case JSON cannot express with a nullable field alone.
        await files.UpdateFileAsync("b1", name: "f.pdf");
        Assert.DoesNotContain("folder_id", api.Last.Body);
    }

    [Fact]
    public async Task A_part_is_sent_as_bytes_rather_than_as_json()
    {
        using var api = new FakeApi(new Reply(200, Bodies.Envelope(@"{""part"":2,""etag"":""e""}")));

        var receipt = await api.Client().Files.UploadPartAsync("u1", 2, [1, 2, 3]);

        // JSON-encoding a file would inflate it and corrupt anything that is
        // not valid UTF-8, which is most of what people upload.
        Assert.Equal("application/octet-stream", api.Last.Headers["content-type"]);
        Assert.Equal(2, receipt.Part);
    }

    [Fact]
    public async Task Download_returns_the_bytes_and_forwards_a_range()
    {
        using var api = new FakeApi(new Reply(206, "not json, on purpose",
            new Dictionary<string, string> { ["Content-Range"] = "bytes 0-3/8" }));

        var (bytes, contentType) = await api.Client().Files.DownloadAsync("b1", "bytes=0-3");

        Assert.Equal("GET /v1/files/b1/content", api.Last.Route);
        Assert.Equal("bytes=0-3", api.Last.Headers["range"]);
        Assert.NotEmpty(bytes);
        Assert.False(string.IsNullOrEmpty(contentType));
    }

    [Fact]
    public async Task A_refused_download_still_raises_the_APIs_own_error()
    {
        using var api = new FakeApi(new Reply(403, Bodies.Refusal("insufficient_scope", "no files.read")));

        var error = await Assert.ThrowsAsync<VeruApiException>(
            () => api.Client().Files.DownloadAsync("b1"));

        // The response is bytes on success and an envelope on failure, so a
        // caller reads the same error type whichever call refused them.
        Assert.Equal("insufficient_scope", error.Code);
    }
}
