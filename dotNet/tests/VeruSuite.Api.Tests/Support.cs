using System.Net;
using System.Text;
using System.Text.Json;
using VeruSuite.Api;

namespace VeruSuite.Api.Tests;

/// <summary>One request the client sent.</summary>
public sealed record Recorded(
    string Method,
    string Path,
    ILookup<string, string> Query,
    IReadOnlyDictionary<string, string> Headers,
    string Body)
{
    public string Route => $"{Method} {Path}";

    public JsonElement Json => JsonDocument.Parse(Body).RootElement;
}

/// <summary>One response the API should give.</summary>
public sealed record Reply(int Status = 200, string? Body = null, IDictionary<string, string>? Headers = null);

/// <summary>
/// Records what the client sent and answers what it was told to.
/// </summary>
/// <remarks>
/// A handler rather than a live server: an HttpRequestMessage carries the
/// method, the URI, every header and the body, which is the whole of what the
/// API would receive. Nothing is asserted about the code calling a mock, only
/// about the request that would go out.
/// </remarks>
public sealed class FakeApi : HttpMessageHandler
{
    private readonly Queue<Reply> _replies = new();

    public FakeApi(params Reply[] replies)
    {
        foreach (var reply in replies)
        {
            _replies.Enqueue(reply);
        }
    }

    public List<Recorded> Requests { get; } = new();

    public List<TimeSpan> Slept { get; } = new();

    /// <summary>
    /// Used once the script runs out.
    /// </summary>
    /// <remarks>
    /// A null data rather than an empty array, because System.Text.Json is
    /// strict about shape: [] deserializes into a list but not into a Message,
    /// and a test that only cares about the request it sent should not have to
    /// describe the response it ignores. Null satisfies every shape, and the
    /// list-returning methods turn it back into an empty list.
    /// </remarks>
    public Reply Default { get; set; } = new(200, """{"data":null,"meta":{"request_id":"req_test"}}""");

    public Recorded Last => Requests[^1];

    /// <summary>A client pointed at this handler, with the waiting taken out.</summary>
    public VeruApiClient Client(int maxRetries = 2)
    {
        var api = new VeruApiClient("vak_live_test_secret", new VeruApiClientOptions
        {
            BaseUrl = "https://api.test.example",
            MaxRetries = maxRetries,
            HttpClient = new HttpClient(this, disposeHandler: false),
        });

        // Replaced rather than shortened: a test asserting that three attempts
        // happen should not also assert how long a coffee takes.
        api.Delay = (wait, _) =>
        {
            Slept.Add(wait);
            return Task.CompletedTask;
        };

        return api;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var body = request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken);

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var pairs = query.AllKeys
            .Where(k => k is not null)
            .SelectMany(k => (query.GetValues(k) ?? Array.Empty<string>()).Select(v => (Key: k!, Value: v)))
            .ToLookup(p => p.Key, p => p.Value);

        var headers = request.Headers
            .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));

        Requests.Add(new Recorded(request.Method.Method, uri.AbsolutePath, pairs, headers, body));

        var reply = _replies.Count > 0 ? _replies.Dequeue() : Default;

        var response = new HttpResponseMessage((HttpStatusCode)reply.Status)
        {
            Content = new StringContent(reply.Body ?? "", Encoding.UTF8, "application/json"),
        };

        foreach (var (name, value) in reply.Headers ?? new Dictionary<string, string>())
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    public void Enqueue(Reply reply) => _replies.Enqueue(reply);
}

public static class Bodies
{
    /// <summary>The shape every successful response arrives in.</summary>
    public static string Envelope(string data, string? nextCursor = null)
    {
        var cursor = nextCursor is null ? "" : $@",""next_cursor"":""{nextCursor}""";
        return $@"{{""data"":{data},""meta"":{{""request_id"":""req_test"",""timestamp"":""t""{cursor}}}}}";
    }

    /// <summary>The shape every refusal arrives in.</summary>
    public static string Refusal(string code, string message = "no", string? details = null)
    {
        var extra = details is null ? "" : $@",""details"":{details}";
        return $@"{{""error"":{{""code"":""{code}"",""message"":""{message}"",""request_id"":""req_test""{extra}}}}}";
    }
}
