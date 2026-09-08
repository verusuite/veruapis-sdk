namespace VeruSuite.Api;

/// <summary>One field the API rejected.</summary>
public sealed record ErrorDetail(string Field, string Message = "");

/// <summary>
/// A refusal from the API.
/// </summary>
/// <remarks>
/// Branch on <see cref="Code"/>, never on <see cref="Exception.Message"/>. The
/// code is a stable identifier; the message is written for a person and may be
/// reworded at any time.
/// <code>
/// try
/// {
///     await api.Mail.SendAsync(message);
/// }
/// catch (VeruApiException err) when (err.IsPermissionProblem)
/// {
///     Console.Error.WriteLine($"this key is missing {err.Code}");
/// }
/// </code>
/// </remarks>
public sealed class VeruApiException : Exception
{
    internal VeruApiException(
        string message,
        string code = "unknown_error",
        int status = 0,
        string requestId = "",
        IReadOnlyList<ErrorDetail>? details = null,
        TimeSpan? retryAfter = null,
        Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Status = status;
        RequestId = requestId;
        Details = details ?? Array.Empty<ErrorDetail>();
        RetryAfter = retryAfter;
    }

    /// <summary>Identifies the failure. This is the field to switch on.</summary>
    public string Code { get; }

    /// <summary>The HTTP status the API answered with.</summary>
    public int Status { get; }

    /// <summary>Identifies the call. Quote it when reporting a problem.</summary>
    public string RequestId { get; }

    /// <summary>The fields that were wrong, when that is the reason.</summary>
    public IReadOnlyList<ErrorDetail> Details { get; }

    /// <summary>
    /// How long the API asked the caller to wait, when it said. Null when it
    /// did not, which is different from zero.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>The credential was missing, malformed, or not one this API issues.</summary>
    public bool IsAuthProblem => Status == 401;

    /// <summary>
    /// The key is valid but was not granted a permission this call needs.
    /// Creating a new key with the right permission is the fix; retrying is not,
    /// which is why this is never retried.
    /// </summary>
    public bool IsPermissionProblem => Status == 403;

    /// <summary>
    /// The record does not exist, or belongs to another workspace. The API does
    /// not distinguish the two, deliberately.
    /// </summary>
    public bool IsNotFound => Status == 404;

    /// <summary>
    /// The plan's rate limit or daily quota is spent. The client already waits
    /// and retries; this is for a caller that would rather slow itself down
    /// than be slowed.
    /// </summary>
    public bool IsRateLimited => Status == 429;

    /// <summary>
    /// Whether sending the same request again could succeed. A 400 or a 403
    /// fails the same way however many times it is sent.
    /// </summary>
    internal bool IsRetryable => Status is 408 or 429 || Status >= 500;

    public override string ToString() =>
        RequestId.Length > 0
            ? $"{Message} ({Code}, request {RequestId})"
            : $"{Message} ({Code})";
}
