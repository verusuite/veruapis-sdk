using System.Net;
using VeruSuite.Api;
using Xunit;

namespace VeruSuite.Api.Tests;

/// <summary>The transport: what goes on the wire, and what happens when it fails.</summary>
public class ClientTests
{
    [Fact]
    public void A_key_is_required()
    {
        // Failing at construction rather than at the first call: an empty
        // environment variable is a configuration mistake, and finding out
        // about it halfway through a job is finding out too late.
        Assert.Throws<ArgumentException>(() => new VeruApiClient(""));
        Assert.Throws<ArgumentException>(() => new VeruApiClient("   "));
        Assert.Throws<ArgumentException>(() => new VeruApiClient(null!));
    }

    [Fact]
    public async Task The_key_travels_as_a_bearer_token()
    {
        using var api = new FakeApi();
        await api.Client().Mail.ListFoldersAsync();

        Assert.Equal("Bearer vak_live_test_secret", api.Last.Headers["authorization"]);
        Assert.Equal("application/json", api.Last.Headers["accept"]);
    }

    [Fact]
    public async Task Writes_carry_an_idempotency_key_and_reads_do_not()
    {
        using var api = new FakeApi();
        var client = api.Client();

        await client.Mail.ListFoldersAsync();
        Assert.DoesNotContain("idempotency-key", api.Last.Headers.Keys);

        await client.Mail.SendAsync(new SendMessage { To = ["a@b.example"] });
        Assert.NotEmpty(api.Last.Headers["idempotency-key"]);
    }

    [Fact]
    public async Task A_supplied_idempotency_key_is_used_verbatim()
    {
        using var api = new FakeApi();
        await api.Client().Mail.SendAsync(new SendMessage { To = ["a@b.example"] }, idempotencyKey: "mine-1");

        Assert.Equal("mine-1", api.Last.Headers["idempotency-key"]);
    }

    [Fact]
    public async Task One_idempotency_key_survives_every_retry()
    {
        // The whole point of the header. A retry that generated a new key would
        // be a second request as far as the server is concerned, and the caller
        // would have sent two emails to avoid sending two emails.
        using var api = new FakeApi(
            new Reply(500, Bodies.Refusal("internal_error")),
            new Reply(500, Bodies.Refusal("internal_error")),
            new Reply(202, Bodies.Envelope(@"{""id"":""m1""}")));

        await api.Client().Mail.SendAsync(new SendMessage { To = ["a@b.example"] });

        var keys = api.Requests.Select(r => r.Headers["idempotency-key"]).Distinct().ToList();
        Assert.Equal(3, api.Requests.Count);
        Assert.Single(keys);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Retryable_statuses_are_retried(int status)
    {
        using var api = new FakeApi(
            new Reply(status, Bodies.Refusal("busy")),
            new Reply(200, Bodies.Envelope("[]")));

        await api.Client().Mail.ListFoldersAsync();
        Assert.Equal(2, api.Requests.Count);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(422)]
    public async Task A_refusal_that_would_fail_again_is_not_retried(int status)
    {
        // A 400 or a 403 fails the same way however many times it is sent.
        // Retrying one only delays the error the caller has to handle.
        using var api = new FakeApi(new Reply(status, Bodies.Refusal("no")));

        await Assert.ThrowsAsync<VeruApiException>(() => api.Client().Mail.ListFoldersAsync());
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task Retries_stop_at_the_limit()
    {
        using var api = new FakeApi();
        api.Default = new Reply(500, Bodies.Refusal("internal_error"));

        var error = await Assert.ThrowsAsync<VeruApiException>(
            () => api.Client(maxRetries: 3).Mail.ListFoldersAsync());

        Assert.Equal(4, api.Requests.Count); // the first attempt plus three retries
        Assert.Equal(500, error.Status);
    }

    [Fact]
    public async Task Retrying_can_be_turned_off()
    {
        using var api = new FakeApi();
        api.Default = new Reply(503, Bodies.Refusal("unavailable"));

        await Assert.ThrowsAsync<VeruApiException>(() => api.Client(maxRetries: 0).Mail.ListFoldersAsync());
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task Retry_after_is_honoured_over_the_backoff()
    {
        // The server knows when it will be ready and the client does not, so a
        // number it supplies beats one this library made up.
        using var api = new FakeApi(
            new Reply(429, Bodies.Refusal("rate_limited"), new Dictionary<string, string> { ["Retry-After"] = "7" }),
            new Reply(200, Bodies.Envelope("[]")));

        await api.Client().Mail.ListFoldersAsync();

        Assert.Equal(TimeSpan.FromSeconds(7), Assert.Single(api.Slept));
    }

    [Fact]
    public async Task A_retry_after_date_is_honoured_too()
    {
        // The RFC allows both spellings, and something upstream will use each.
        using var api = new FakeApi(
            new Reply(429, Bodies.Refusal("rate_limited"),
                new Dictionary<string, string> { ["Retry-After"] = "Wed, 21 Oct 2099 07:28:00 GMT" }),
            new Reply(200, Bodies.Envelope("[]")));

        await api.Client().Mail.ListFoldersAsync();

        Assert.True(Assert.Single(api.Slept) > TimeSpan.Zero);
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("-5")]
    [InlineData("Wed, 21 Oct 2015 07:28:00 GMT")]
    public async Task An_unusable_retry_after_falls_back_to_the_backoff(string header)
    {
        using var api = new FakeApi(
            new Reply(429, Bodies.Refusal("rate_limited"), new Dictionary<string, string> { ["Retry-After"] = header }),
            new Reply(200, Bodies.Envelope("[]")));

        await api.Client().Mail.ListFoldersAsync();

        var waited = Assert.Single(api.Slept);
        Assert.True(waited > TimeSpan.Zero && waited <= TimeSpan.FromSeconds(0.5));
    }

    [Fact]
    public async Task An_unreachable_server_is_retried_then_reported()
    {
        using var handler = new ThrowingHandler();
        using var api = new VeruApiClient("k", new VeruApiClientOptions
        {
            BaseUrl = "https://api.test.example",
            MaxRetries = 1,
            HttpClient = new HttpClient(handler, disposeHandler: false),
        });

        var slept = new List<TimeSpan>();
        api.Delay = (wait, _) => { slept.Add(wait); return Task.CompletedTask; };

        var error = await Assert.ThrowsAsync<VeruApiException>(() => api.Mail.ListFoldersAsync());

        Assert.Equal("transport_error", error.Code);
        Assert.Equal(2, handler.Attempts);
        Assert.Single(slept);
    }

    [Fact]
    public void Backoff_grows_and_is_capped()
    {
        Assert.True(VeruApiClient.Backoff(0) <= TimeSpan.FromSeconds(0.5));
        Assert.True(VeruApiClient.Backoff(1) <= TimeSpan.FromSeconds(1));

        // Capped, so a long-lived process does not end up sleeping for minutes.
        Assert.True(VeruApiClient.Backoff(30) <= TimeSpan.FromSeconds(8));

        // Jittered, so a fleet of clients does not retry in step and turn a
        // brief failure into a sustained one.
        var samples = Enumerable.Range(0, 50).Select(_ => VeruApiClient.Backoff(4)).Distinct().Count();
        Assert.True(samples > 1);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Attempts++;
            throw new HttpRequestException("no route to host");
        }
    }
}

public class ErrorTests
{
    [Fact]
    public async Task The_envelope_is_read_into_the_error()
    {
        using var api = new FakeApi(new Reply(403, Bodies.Refusal(
            "insufficient_scope", "This key cannot send mail.",
            details: @"[{""field"":""scope"",""message"":""mail.send""}]")));

        var error = await Assert.ThrowsAsync<VeruApiException>(() => api.Client().Mail.ListFoldersAsync());

        Assert.Equal("insufficient_scope", error.Code);
        Assert.Equal(403, error.Status);
        Assert.Equal("req_test", error.RequestId);
        Assert.Equal("scope", Assert.Single(error.Details).Field);
        Assert.Contains("insufficient_scope", error.ToString());
        Assert.Contains("req_test", error.ToString());
    }

    [Theory]
    [InlineData(401, nameof(VeruApiException.IsAuthProblem))]
    [InlineData(403, nameof(VeruApiException.IsPermissionProblem))]
    [InlineData(404, nameof(VeruApiException.IsNotFound))]
    [InlineData(429, nameof(VeruApiException.IsRateLimited))]
    public async Task The_four_questions_worth_asking(int status, string expected)
    {
        using var api = new FakeApi(new Reply(status, Bodies.Refusal("x")));

        var error = await Assert.ThrowsAsync<VeruApiException>(
            () => api.Client(maxRetries: 0).Mail.ListFoldersAsync());

        var all = new Dictionary<string, bool>
        {
            [nameof(VeruApiException.IsAuthProblem)] = error.IsAuthProblem,
            [nameof(VeruApiException.IsPermissionProblem)] = error.IsPermissionProblem,
            [nameof(VeruApiException.IsNotFound)] = error.IsNotFound,
            [nameof(VeruApiException.IsRateLimited)] = error.IsRateLimited,
        };

        Assert.True(all[expected]);

        // And exactly one of them is true, so a caller branching on the wrong
        // one finds out here rather than in production.
        Assert.Single(all, pair => pair.Value);
    }

    [Theory]
    [InlineData("<html>502 Bad Gateway</html>")]
    [InlineData("")]
    [InlineData(@"{""nope"":true}")]
    [InlineData("[1,2,3]")]
    [InlineData(@"{""error"":{""message"":""no code here""}}")]
    public async Task A_response_that_is_not_the_api_says_so(string body)
    {
        // A proxy, an error page, a misrouted request. Reporting that beats
        // reporting a JSON parse failure nobody can act on.
        using var api = new FakeApi(new Reply(502, body));

        var error = await Assert.ThrowsAsync<VeruApiException>(
            () => api.Client(maxRetries: 0).Mail.ListFoldersAsync());

        Assert.Equal("unreadable_response", error.Code);
        Assert.Contains("502", error.Message);
    }

    [Fact]
    public async Task An_error_without_a_request_id_still_reads()
    {
        using var api = new FakeApi(new Reply(400, @"{""error"":{""code"":""bad_request"",""message"":""no""}}"));

        var error = await Assert.ThrowsAsync<VeruApiException>(() => api.Client().Mail.ListFoldersAsync());

        Assert.Equal("no (bad_request)", error.ToString());
    }
}

public class QueryTests
{
    [Fact]
    public void A_list_repeats_rather_than_joining()
    {
        // The API reads ?folder_id=a&folder_id=b as two values and
        // ?folder_id=a,b as one folder called "a,b".
        var rendered = VeruApiClient.QueryString(new Dictionary<string, object?>
        {
            ["folder_id"] = new List<string> { "a", "b" },
        });

        Assert.Equal("folder_id=a&folder_id=b", rendered);
    }

    [Fact]
    public void Empty_values_are_left_out()
    {
        var rendered = VeruApiClient.QueryString(new Dictionary<string, object?>
        {
            ["a"] = "",
            ["b"] = null,
            ["c"] = 0,
            ["d"] = false,
            ["e"] = new List<string>(),
            ["f"] = "kept",
        });

        Assert.Equal("f=kept", rendered);
    }

    [Fact]
    public void True_is_sent_as_the_word()
    {
        Assert.Equal("unread=true", VeruApiClient.QueryString(new Dictionary<string, object?> { ["unread"] = true }));
    }

    [Fact]
    public void Nothing_at_all_renders_to_nothing()
    {
        Assert.Equal("", VeruApiClient.QueryString(null));
        Assert.Equal("", VeruApiClient.QueryString(new Dictionary<string, object?>()));
    }

    [Fact]
    public async Task An_id_with_a_slash_is_still_one_id()
    {
        using var api = new FakeApi();
        await api.Client().Mail.GetMessageAsync("a/b c");

        Assert.Equal("/v1/messages/a%2Fb%20c", api.Last.Path);
    }
}

public class EnvelopeTests
{
    [Fact]
    public async Task No_content_has_no_body_to_decode()
    {
        using var api = new FakeApi(new Reply(204));
        await api.Client().Calendar.DeleteEventAsync("c1", "e1");

        Assert.Equal("DELETE", api.Last.Method);
    }

    [Fact]
    public async Task Meta_comes_back_alongside_the_data()
    {
        using var api = new FakeApi(new Reply(200, Bodies.Envelope("[]", nextCursor: "c2")));

        var (_, meta) = await api.Client().Mail.ListDraftsAsync();

        Assert.Equal("c2", meta.NextCursor);
        Assert.Equal("req_test", meta.RequestId);
    }

    [Fact]
    public async Task An_empty_body_on_success_is_not_a_failure()
    {
        using var api = new FakeApi(new Reply(200, ""));

        var folders = await api.Client().Mail.ListFoldersAsync();
        Assert.Empty(folders);
    }

    [Fact]
    public async Task A_supplied_http_client_is_left_for_its_owner_to_dispose()
    {
        // A program that hands over its HttpClient expects to keep using it.
        using var handler = new FakeApi();
        var shared = new HttpClient(handler, disposeHandler: false);

        var api = new VeruApiClient("k", new VeruApiClientOptions
        {
            BaseUrl = "https://api.test.example",
            HttpClient = shared,
        });
        api.Dispose();

        // Still usable, which it would not be had the client disposed it.
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.test.example/v1/folders");
        var response = await shared.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void A_client_that_made_its_own_http_client_disposes_it()
    {
        var api = new VeruApiClient("k");
        api.Dispose();
        api.Dispose(); // and disposing twice is not an error
    }
}
