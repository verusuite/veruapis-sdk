namespace VeruSuite.Api;

/// <summary>The contents of a spreadsheet: ranges, appends and structural changes.</summary>
/// <remarks>
/// A spreadsheet's lifecycle — creating, renaming, trashing — is documents,
/// because a spreadsheet is a document. These are its cells.
/// <para>
/// Worth knowing before a key is granted: writing values needs
/// <c>sheets.values.write</c> and merges cleanly with whatever else is
/// happening in the document, while inserting or deleting rows needs
/// <c>sheets.structure.write</c> and moves everything below them. A script that
/// fills in a weekly figure should hold only the first.
/// </para>
/// </remarks>
public sealed class SpreadsheetsClient
{
    private readonly VeruApiClient _api;

    internal SpreadsheetsClient(VeruApiClient api) => _api = api;

    /// <summary>What is stored in the cell, so a formula comes back as its own text.</summary>
    public const string RenderStored = "stored";

    /// <summary>
    /// Evaluated results, which cost a pass over the whole workbook and are
    /// unavailable when no formula engine is running.
    /// </summary>
    public const string RenderComputed = "computed";

    /// <summary>The workbook: every sheet with its extent and merges.</summary>
    /// <remarks>
    /// This is how the tabs are listed; there is no separate call for them.
    /// Read it first to learn the sheet names an A1 range needs.
    /// </remarks>
    public async Task<Workbook> GetAsync(
        string spreadsheetId,
        CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<Workbook>(
            HttpMethod.Get, SheetPath(spreadsheetId), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return data ?? new Workbook();
    }

    /// <summary>The document's change token.</summary>
    /// <remarks>
    /// Two uses, one value: poll it to notice somebody else's change — there
    /// are no webhooks for spreadsheets — and pass it to
    /// <see cref="ApplyStructureAsync"/>, which refuses to work without it.
    /// </remarks>
    public async Task<DocumentState> StateAsync(
        string spreadsheetId,
        CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<DocumentState>(
            HttpMethod.Get, SheetPath(spreadsheetId) + "/state", cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return data ?? new DocumentState();
    }

    /// <summary>Reads one A1 range.</summary>
    public async Task<ValueRange> ValuesAsync(
        string spreadsheetId,
        string range,
        string render = RenderStored,
        CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<ValueRange>(
            HttpMethod.Get, ValuesPath(spreadsheetId, range), RenderQuery(render),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return data ?? new ValueRange();
    }

    /// <summary>Reads several ranges in one call.</summary>
    /// <remarks>
    /// The cell cap applies to the whole call, and a computed read evaluates
    /// the workbook once for all of them rather than once per range.
    /// </remarks>
    public async Task<IReadOnlyList<ValueRange>> BatchValuesAsync(
        string spreadsheetId,
        IEnumerable<string> ranges,
        string render = RenderStored,
        CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<BatchValues>(
            HttpMethod.Post,
            SheetPath(spreadsheetId) + "/values/batch-get",
            RenderQuery(render),
            new { ranges = ranges.ToList() },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return data?.ValueRanges ?? new List<ValueRange>();
    }

    /// <summary>Overwrites one range.</summary>
    /// <remarks>
    /// Values are anchored at the range's top-left corner, and a block shorter
    /// than the range leaves the rest untouched: writing two rows into a
    /// ten-row range writes two rows. Clearing is a separate call for that
    /// reason.
    /// <para>
    /// An empty string deletes a cell rather than storing a blank, which would
    /// keep it inside the sheet's used extent and widen every unbounded range
    /// from then on.
    /// </para>
    /// </remarks>
    public async Task<WriteResult> WriteAsync(
        string spreadsheetId,
        string range,
        IEnumerable<IEnumerable<object?>> values,
        CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<WriteResult>(
            HttpMethod.Put, ValuesPath(spreadsheetId, range), body: Rows(values),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return data ?? new WriteResult();
    }

    /// <summary>
    /// Writes several ranges as one document write, so somebody with the sheet
    /// open sees one change rather than a flicker of several.
    /// </summary>
    public async Task<WriteResult> BatchWriteAsync(
        string spreadsheetId,
        IEnumerable<ValueRange> data,
        CancellationToken cancellationToken = default)
    {
        var body = data.Select(item => new { range = item.Range, values = item.Values }).ToList();
        var (out_, _) = await _api.SendAsync<WriteResult>(
            HttpMethod.Post,
            SheetPath(spreadsheetId) + "/values/batch-update",
            body: new { data = body },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return out_ ?? new WriteResult();
    }

    /// <summary>Adds rows after the last populated row of the range's own columns.</summary>
    /// <remarks>
    /// Of its own columns, not the sheet's: an unrelated note in column Z does
    /// not push the table down. This is the call a log or a nightly export
    /// wants — it needs no read first, and two appends cannot overwrite each
    /// other.
    /// </remarks>
    public async Task<WriteResult> AppendAsync(
        string spreadsheetId,
        string range,
        IEnumerable<IEnumerable<object?>> values,
        CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<WriteResult>(
            HttpMethod.Post, ValuesPath(spreadsheetId, range) + "/append", body: Rows(values),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return data ?? new WriteResult();
    }

    /// <summary>Empties a range and leaves its formatting.</summary>
    /// <remarks>A template keeps its headers, colours and number formats.</remarks>
    public async Task<WriteResult> ClearAsync(
        string spreadsheetId,
        string range,
        CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<WriteResult>(
            HttpMethod.Post, ValuesPath(spreadsheetId, range) + "/clear",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return data ?? new WriteResult();
    }

    /// <summary>
    /// Inserts and deletes rows, columns and sheets, and changes merges,
    /// sorting and formatting.
    /// </summary>
    /// <remarks>
    /// <paramref name="state"/> comes from <see cref="StateAsync"/> and is
    /// required: these are the changes that move data other requests address by
    /// position, so one applied to a document that has moved on merges cleanly
    /// into a corrupt grid. A stale token is refused with a conflict — read the
    /// state again and reapply.
    /// <para>
    /// Requests apply in order and each sees the effect of the one before, so
    /// adding a sheet and formatting it in one batch works.
    /// </para>
    /// </remarks>
    public async Task<StructureResult> ApplyStructureAsync(
        string spreadsheetId,
        string state,
        IEnumerable<IDictionary<string, object?>> requests,
        CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<StructureResult>(
            HttpMethod.Post,
            SheetPath(spreadsheetId) + "/batch-update",
            body: new { requests = requests.ToList() },
            ifMatch: state,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return data ?? new StructureResult();
    }

    private static object Rows(IEnumerable<IEnumerable<object?>> values) =>
        new { values = values.Select(row => row.ToList()).ToList() };

    private static string SheetPath(string spreadsheetId) =>
        "/v1/spreadsheets/" + Uri.EscapeDataString(spreadsheetId);

    /// <summary>
    /// Builds a range path, escaping the range as one segment.
    /// </summary>
    /// <remarks>
    /// A1 notation carries characters a URL reads as structure — a quoted sheet
    /// name, a space, a colon — and a range is never a path.
    /// </remarks>
    private static string ValuesPath(string spreadsheetId, string range) =>
        SheetPath(spreadsheetId) + "/values/" + Uri.EscapeDataString(range);

    private static IEnumerable<KeyValuePair<string, object?>>? RenderQuery(string render) =>
        string.IsNullOrEmpty(render) || render == RenderStored
            ? null
            : new[] { new KeyValuePair<string, object?>("value_render", render) };
}
