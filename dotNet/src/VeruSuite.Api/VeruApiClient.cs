using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeruSuite.Api;

/// <summary>How a <see cref="VeruApiClient"/> behaves.</summary>
public sealed record VeruApiClientOptions
{
    /// <summary>Somewhere other than production.</summary>
    public string BaseUrl { get; init; } = VeruApiClient.DefaultBaseUrl;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many times a retryable failure is tried again. Only 429, 408 and 5xx
    /// are ever retried, and only after the delay the server asked for. Zero
    /// disables retrying entirely.
    /// </summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>
    /// The <see cref="HttpClient"/> to send with. Worth supplying in any
    /// program that already has one: connection pooling, proxies and timeouts
    /// are usually decided once for a whole process rather than per library. A
    /// supplied client is not disposed by this one.
    /// </summary>
    public HttpClient? HttpClient { get; init; }
}

/// <summary>
/// Calls the API.
/// </summary>
/// <remarks>
/// Every call is authorised by a key created at veruapis.com and acts as the
/// person who created it, limited to the permissions they granted. Nothing here
/// widens that key.
/// <code>
/// var api = new VeruApiClient(Environment.GetEnvironmentVariable("VERUAPIS_KEY")!);
///
/// foreach (var folder in await api.Mail.ListFoldersAsync())
///     Console.WriteLine($"{folder.Name} {folder.UnreadCount}");
/// </code>
/// The client depends on nothing outside the base class library. That is worth
/// more in a library than anywhere else: every dependency here becomes one in
/// every program that installs it.
/// </remarks>
public sealed class VeruApiClient : IDisposable
{
    /// <summary>The production API.</summary>
    public const string DefaultBaseUrl = "https://api.veruapis.com";

    // A client library should not be the reason a program runs out of memory
    // because something upstream answered with a stream.
    private const int MaxResponseBytes = 32 * 1024 * 1024;

    internal static readonly JsonSerializerOptions Json = new()
    {
        // One place, rather than an attribute on every property. C# is
        // PascalCase and this API is snake_case, and that is the whole of the
        // difference between them.
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,

        // Null means "not set", and an absent field and a null one are
        // different requests on a partial update.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly int _maxRetries;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    // Injected so the retry behaviour can be tested without the test taking as
    // long as the backoff it is asserting.
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } =
        (wait, token) => Task.Delay(wait, token);

    /// <summary>Builds a client for the given key.</summary>
    public VeruApiClient(string apiKey, VeruApiClientOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // Failing at construction rather than at the first call: an empty
            // environment variable is a configuration mistake, and finding out
            // halfway through a job is finding out too late.
            throw new ArgumentException("An apiKey is required. Create one at veruapis.com.", nameof(apiKey));
        }

        options ??= new VeruApiClientOptions();

        _apiKey = apiKey.Trim();
        _baseUrl = options.BaseUrl.TrimEnd('/');
        _maxRetries = Math.Max(0, options.MaxRetries);

        _ownsHttpClient = options.HttpClient is null;
        _http = options.HttpClient ?? new HttpClient();
        if (_ownsHttpClient)
        {
            _http.Timeout = options.Timeout;
        }

        Mail = new MailClient(this);
        Calendar = new CalendarClient(this);
        Spreadsheets = new SpreadsheetsClient(this);
        Documents = new DocumentsClient(this);
        Files = new FilesClient(this);
        Identity = new IdentityClient(this);
        Contacts = new ContactsClient(this);
    }

    /// <summary>Folders, messages, drafts and sending.</summary>
    public MailClient Mail { get; }

    /// <summary>Calendars, events and availability.</summary>
    public CalendarClient Calendar { get; }

    /// <summary>Documents and spreadsheets, their comments and sharing.</summary>
    public DocumentsClient Documents { get; }

    /// <summary>Uploaded files, their folders, and resumable upload.</summary>
    public FilesClient Files { get; }

    /// <summary>Who the key acts as, and the workspace's groups.</summary>
    public IdentityClient Identity { get; }

    /// <summary>Address books and the people in them.</summary>
    public ContactsClient Contacts { get; }

    /// <summary>The contents of a spreadsheet: ranges, appends and structural changes.</summary>
    public SpreadsheetsClient Spreadsheets { get; }

    /// <summary>
    /// Performs one request and returns its data and meta.
    /// </summary>
    /// <remarks>
    /// Public, because a client is allowed to lag the API: an endpoint nothing
    /// here wraps is still one call away.
    /// </remarks>
    /// <summary>Fetches a file's bytes and its content type.</summary>
    /// <remarks>
    /// The one shape in this API that is not an envelope. A refusal still
    /// arrives as one and is still thrown as a <see cref="VeruApiException"/>,
    /// so the only difference a caller sees is what they get on success.
    /// <para>
    /// One attempt, unlike <see cref="SendAsync{T}"/>. A download that failed
    /// halfway has already handed back part of a file, and starting again would
    /// join two prefixes into something that is neither — the caller passes a
    /// range instead, which is why one is supported.
    /// </para>
    /// </remarks>
    public async Task<(byte[] Bytes, string ContentType)> DownloadAsync(
        string path,
        string? range = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + path);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        if (range is not null)
        {
            request.Headers.TryAddWithoutValidation("Range", range);
        }

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        if ((int)response.StatusCode >= 400)
        {
            throw ErrorReader.Read((int)response.StatusCode, response, bytes);
        }

        return (bytes, response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
    }

    public async Task<(T? Data, Meta Meta)> SendAsync<T>(
        HttpMethod method,
        string path,
        IEnumerable<KeyValuePair<string, object?>>? query = null,
        object? body = null,
        string? idempotencyKey = null,
        string? ifMatch = null,
        byte[]? rawBody = null,
        string? contentType = null,
        CancellationToken cancellationToken = default)
    {
        var target = _baseUrl + path;
        var encoded = QueryString(query);
        if (encoded.Length > 0)
        {
            target += "?" + encoded;
        }

        // The one call that carries bytes rather than JSON is a part of a
        // resumable upload; everything else is serialised.
        byte[]? payload = body is not null
            ? JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), Json)
            : rawBody;

        // One key for every attempt, not one per attempt. A retry that
        // generated a new key would be a second request as far as the server is
        // concerned, which defeats the point of sending one.
        if (idempotencyKey is null && method != HttpMethod.Get && method != HttpMethod.Delete)
        {
            idempotencyKey = Guid.NewGuid().ToString();
        }

        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;

            try
            {
                using var request = new HttpRequestMessage(method, target);
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                request.Headers.TryAddWithoutValidation("User-Agent", "veruapis-dotnet");
                if (idempotencyKey is not null)
                {
                    request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
                }
                if (ifMatch is not null)
                {
                    // One endpoint takes it, for a reason worth stating:
                    // inserting or deleting rows moves everything below them,
                    // so a structural change applied to a document that has
                    // moved on merges cleanly into a corrupt grid. A stale
                    // token answers 409 and nothing is applied.
                    request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
                }
                if (payload is not null)
                {
                    // A fresh content per attempt: the previous one is consumed.
                    request.Content = new ByteArrayContent(payload);
                    request.Content.Headers.TryAddWithoutValidation(
                        "Content-Type",
                        body is not null ? "application/json" : contentType ?? "application/octet-stream");
                }

                response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException
                                        && !cancellationToken.IsCancellationRequested)
            {
                // A transport failure. Worth another try, since nothing about
                // the request itself was refused.
                if (attempt >= _maxRetries)
                {
                    throw new VeruApiException(
                        $"could not reach {_baseUrl}: {exc.Message}",
                        code: "transport_error",
                        inner: exc);
                }

                await Delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                var raw = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);

                if (status < 400)
                {
                    return Envelope<T>(status, raw);
                }

                var error = ErrorReader.Read(status, response, raw);

                // Retried only when the server said to. A 400 or a 403 fails
                // the same way however many times it is sent.
                if (!error.IsRetryable || attempt >= _maxRetries)
                {
                    throw error;
                }

                await Delay(error.RetryAfter ?? Backoff(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Walks a cursor-paged listing.
    /// </summary>
    /// <remarks>
    /// Cursor rather than an offset: rows arriving mid-walk shift an offset and
    /// a page gets skipped, which is a data-loss bug that looks like nothing at
    /// all.
    /// </remarks>
    public async IAsyncEnumerable<T> PaginateAsync<T>(
        string path,
        IEnumerable<KeyValuePair<string, object?>>? query = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var parameters = query?.Where(p => p.Key != "cursor").ToList() ?? new List<KeyValuePair<string, object?>>();
        var cursor = query?.FirstOrDefault(p => p.Key == "cursor").Value as string;

        while (true)
        {
            var page = new List<KeyValuePair<string, object?>>(parameters);
            if (!string.IsNullOrEmpty(cursor))
            {
                page.Add(new KeyValuePair<string, object?>("cursor", cursor));
            }

            var (items, meta) = await SendAsync<List<T>>(HttpMethod.Get, path, page, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            items ??= new List<T>();
            foreach (var item in items)
            {
                yield return item;
            }

            // An empty page carrying a cursor would loop forever, so the
            // absence of rows ends the walk as surely as the absence of one.
            if (string.IsNullOrEmpty(meta.NextCursor) || items.Count == 0)
            {
                yield break;
            }

            cursor = meta.NextCursor;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();

        var chunk = new byte[81920];
        int read;
        while (buffer.Length < MaxResponseBytes
               && (read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static (T? Data, Meta Meta) Envelope<T>(int status, byte[] raw)
    {
        // 204 has no body to decode, and deserializing nothing throws rather
        // than returning an empty value.
        if (status == (int)HttpStatusCode.NoContent || raw.Length == 0)
        {
            return (default, new Meta());
        }

        var envelope = JsonSerializer.Deserialize<Envelope<T>>(raw, Json);
        return envelope is null ? (default, new Meta()) : (envelope.Data, envelope.Meta);
    }

    /// <summary>
    /// Renders a query, skipping empty values and repeating a list.
    /// </summary>
    /// <remarks>
    /// Repeating rather than joining: the API reads ?folder_id=a&amp;folder_id=b
    /// as two values and ?folder_id=a,b as one folder called "a,b".
    /// </remarks>
    internal static string QueryString(IEnumerable<KeyValuePair<string, object?>>? query)
    {
        if (query is null)
        {
            return "";
        }

        var parts = new List<string>();

        foreach (var (key, value) in query)
        {
            switch (value)
            {
                case null:
                    break;
                case bool flag:
                    // False is "not set" here, the same as it is everywhere
                    // else, so it is left out rather than sent as false.
                    if (flag)
                    {
                        parts.Add($"{Uri.EscapeDataString(key)}=true");
                    }
                    break;
                case string text:
                    if (text.Length > 0)
                    {
                        parts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(text)}");
                    }
                    break;
                case IEnumerable<string> items:
                    foreach (var item in items.Where(i => !string.IsNullOrEmpty(i)))
                    {
                        parts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(item)}");
                    }
                    break;
                default:
                    var rendered = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
                    // An unset number is zero, and ?limit=0 is a request for no
                    // rows at all.
                    if (rendered.Length > 0 && rendered != "0")
                    {
                        parts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(rendered)}");
                    }
                    break;
            }
        }

        return string.Join("&", parts);
    }

    /// <summary>
    /// Exponential with jitter, so a fleet of clients does not retry in step
    /// and turn a brief failure into a sustained one.
    /// </summary>
    internal static TimeSpan Backoff(int attempt)
    {
        var seconds = Math.Min(0.5 * Math.Pow(2, Math.Min(attempt, 16)), 8.0);
        return TimeSpan.FromSeconds(seconds / 2 + Random.Shared.NextDouble() * (seconds / 2));
    }

    /// <summary>Escapes one path segment. An id with a slash in it is still one id.</summary>
    internal static string Escape(string value) => Uri.EscapeDataString(value);

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
