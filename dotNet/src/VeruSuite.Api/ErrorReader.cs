using System.Globalization;
using System.Text.Json;

namespace VeruSuite.Api;

/// <summary>Reads the API's error envelope out of a failed response.</summary>
internal static class ErrorReader
{
    public static VeruApiException Read(int status, HttpResponseMessage response, byte[] body)
    {
        var retryAfter = RetryAfter(response);

        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                && code.GetString() is { Length: > 0 } codeText)
            {
                return new VeruApiException(
                    Text(error, "message") is { Length: > 0 } message
                        ? message
                        : $"the server answered {status}",
                    code: codeText,
                    status: status,
                    requestId: Text(error, "request_id") ?? "",
                    details: Details(error),
                    retryAfter: retryAfter);
            }
        }
        catch (JsonException)
        {
            // Falls through to the same answer as a well-formed response that
            // is not an error envelope.
        }

        // Something upstream of the API answered: a proxy, an error page, a
        // misrouted request. Saying so beats reporting a parse failure nobody
        // can act on.
        return new VeruApiException(
            $"the server answered {status}",
            code: "unreadable_response",
            status: status,
            retryAfter: retryAfter);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<ErrorDetail> Details(JsonElement error)
    {
        if (!error.TryGetProperty("details", out var details) || details.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ErrorDetail>();
        }

        var found = new List<ErrorDetail>();
        foreach (var item in details.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                found.Add(new ErrorDetail(Text(item, "field") ?? "", Text(item, "message") ?? ""));
            }
        }

        return found;
    }

    /// <summary>Reads the header, which the RFC allows as seconds or as a date.</summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values))
        {
            return null;
        }

        var raw = values.FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
        }

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
        {
            var wait = when - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : null;
        }

        // Neither a number nor a date. Something upstream wrote the header and
        // the backoff is a better answer than failing on it.
        return null;
    }
}
