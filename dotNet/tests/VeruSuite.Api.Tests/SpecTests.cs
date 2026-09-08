using System.Text.Json;
using System.Text.RegularExpressions;
using VeruSuite.Api;
using Xunit;

namespace VeruSuite.Api.Tests;

/// <summary>
/// The client, checked against the API's own description.
/// </summary>
/// <remarks>
/// Nothing here is generated, so this is what keeps the hand-written client
/// honest. A route this client calls that the API does not serve is a 404
/// waiting for whoever calls that method, and it fails the build.
/// <para>
/// The routes are collected by calling every method against a recording
/// handler, not by scanning the source. The Go client learned this the hard
/// way: a path built by concatenation gives up only its first literal to a
/// regular expression, so real routes were reported as unwrapped and the check
/// passed on truncated prefixes that happened to match. Running the client is
/// exact, and it exercises every method as a side effect.
/// </para>
/// <para>
/// It reads the JSON rather than the YAML, as the Go and Python clients do, so
/// the test project needs no parser for it. Both files are written by one run
/// of "veruapis spec" and cannot disagree.
/// </para>
/// </remarks>
public class SpecTests
{
    // The values EveryCall passes, so a concrete path can be turned back into
    // the templated one the specification describes.
    private static readonly HashSet<string> Ids = ["f1", "m1", "c1", "e1"];

    /// <summary>
    /// One invocation of every method this client offers.
    /// </summary>
    /// <remarks>
    /// Adding a method here is the price of adding one to the client, and it is
    /// the right price: an unlisted method is one nothing has ever called,
    /// which is how a typo in a path ships.
    /// </remarks>
    private static async Task EveryCallAsync(VeruApiClient api)
    {
        await api.Mail.ListFoldersAsync();
        await api.Mail.ListMessagesAsync(new ListMessagesQuery { FolderId = ["f1"] });
        await api.Mail.GetMessageAsync("m1");
        await api.Mail.ListAttachmentsAsync("m1");
        await api.Mail.ListDraftsAsync(limit: 10);
        await api.Mail.SendAsync(new SendMessage { To = ["a@b.example"] });

        await api.Calendar.ListCalendarsAsync();
        await api.Calendar.GetCalendarAsync("c1");
        await api.Calendar.ListEventsAsync(new ListEventsQuery { Start = "s", End = "e" });
        await api.Calendar.GetEventAsync("c1", "e1");
        await api.Calendar.CreateEventAsync("c1", new Event { Summary = "x" });
        await api.Calendar.UpdateEventAsync("c1", "e1", new Event { Summary = "y" });
        await api.Calendar.DeleteEventAsync("c1", "e1");
        await api.Calendar.FreeBusyAsync(["a@b.example"], "s", "e");

        // The iterators, drained so their first request is made.
        await foreach (var _ in api.Mail.MessagesAsync(new ListMessagesQuery { FolderId = ["f1"] }))
        {
            break;
        }
        await foreach (var _ in api.Calendar.EventsAsync(new ListEventsQuery { Start = "s", End = "e" }))
        {
            break;
        }
    }

    private static async Task<HashSet<string>> RoutesTheClientCallsAsync()
    {
        // The harness answers a null data, which satisfies every shape here:
        // the list methods turn it into an empty list, the object methods into
        // null, and the absent cursor stops the iterators after one page. What
        // is being collected is the requests, not the answers.
        using var api = new FakeApi();

        await EveryCallAsync(api.Client(maxRetries: 0));

        return api.Requests.Select(r => $"{r.Method} {Templated(r.Path)}").ToHashSet();
    }

    /// <summary>Turns /v1/messages/m1 into /v1/messages/{}, the shape the spec uses.</summary>
    private static string Templated(string path) =>
        string.Join("/", path.Split('/').Select(part => Ids.Contains(part) ? "{}" : part)).TrimEnd('/');

    /// <summary>Every route the API serves, minus the ones it describes but does not.</summary>
    private static HashSet<string> Documented()
    {
        var spec = JsonDocument.Parse(File.ReadAllText(SpecPath()));
        var methods = new[] { "get", "post", "put", "patch", "delete" };

        var found = new HashSet<string>();

        foreach (var path in spec.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (!methods.Contains(operation.Name.ToLowerInvariant()))
                {
                    continue;
                }

                // A route the server does not implement yet is marked
                // deprecated in the description. The SDK should not offer it,
                // so it does not count as documented for this purpose.
                if (operation.Value.TryGetProperty("deprecated", out var deprecated)
                    && deprecated.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                var normalised = Regex.Replace(path.Name, @"\{[^}]*\}", "{}").TrimEnd('/');
                found.Add($"{operation.Name.ToUpperInvariant()} {normalised}");
            }
        }

        return found;
    }

    /// <summary>
    /// Walks up from the test assembly to the repository's spec directory, so
    /// the path holds wherever the build output lands.
    /// </summary>
    private static string SpecPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "spec", "openapi.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "no spec/openapi.json above the test assembly; regenerate with\n" +
            "  cd ../../veruapis && go run ./cmd/veruapis spec ../veruapis-sdks/spec/openapi.yaml");
    }

    [Fact]
    public async Task Every_route_the_client_calls_is_documented()
    {
        var documented = Documented();
        Assert.NotEmpty(documented);

        var unknown = (await RoutesTheClientCallsAsync()).Except(documented).Order().ToList();

        Assert.True(
            unknown.Count == 0,
            "these are 404s waiting to happen. Fix the client, or regenerate the "
            + "specification if the API really did change:\n  " + string.Join("\n  ", unknown));
    }

    [Fact]
    public async Task Endpoints_not_wrapped_yet()
    {
        // Reported, not failed: the client is allowed to lag, and SendAsync
        // reaches anything it has not wrapped.
        var missing = Documented().Except(await RoutesTheClientCallsAsync()).Order().ToList();

        if (missing.Count > 0)
        {
            Console.WriteLine($"{missing.Count} endpoint(s) not wrapped yet (SendAsync reaches them):");
            foreach (var route in missing)
            {
                Console.WriteLine($"  {route}");
            }
        }
    }

    [Fact]
    public async Task The_client_covers_what_the_other_clients_cover()
    {
        // Four clients drifting apart is the failure this repository exists to
        // prevent, and the Go client is the one the others were written
        // against. If Go grows a method, this should grow one too.
        var repository = new DirectoryInfo(SpecPath()).Parent!.Parent!;
        var goFiles = Directory.GetFiles(Path.Combine(repository.FullName, "go"), "*.go")
            .Where(f => !f.EndsWith("_test.go", StringComparison.Ordinal));

        var goRoutes = new HashSet<string>();
        foreach (var file in goFiles)
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"Path:\s*""([^""]+)"""))
            {
                goRoutes.Add(match.Groups[1].Value.TrimEnd('/'));
            }
        }

        Assert.NotEmpty(goRoutes);

        // Only the fixed prefixes are comparable, since Go builds the rest by
        // concatenation. A prefix this client never touches is a gap.
        var mine = (await RoutesTheClientCallsAsync())
            .Select(r => r.Split(' ', 2)[1])
            .ToList();

        var uncovered = goRoutes
            .Where(route => !mine.Any(m => m.StartsWith(route, StringComparison.Ordinal)))
            .Order()
            .ToList();

        Assert.True(
            uncovered.Count == 0,
            "the Go client calls these and this one never does:\n  " + string.Join("\n  ", uncovered));
    }
}
