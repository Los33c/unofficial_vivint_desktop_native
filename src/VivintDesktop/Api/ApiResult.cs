using System.Text.Json;

namespace VivintDesktop.Api;

/// <summary>Result of an API call: status code, raw body, and parsed JSON (if any).</summary>
public sealed class ApiResult
{
    public int Status { get; init; }
    public string RawBody { get; init; } = "";
    public JsonElement? Json { get; init; }
    public bool Ok => Status is >= 200 and < 300;

    public string Pretty()
    {
        if (Json is { } j)
        {
            try
            {
                return JsonSerializer.Serialize(j, PrettyOptions);
            }
            catch { /* fall through */ }
        }
        return RawBody;
    }

    public static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };

    public static ApiResult From(int status, string body)
    {
        JsonElement? json = null;
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                json = doc.RootElement.Clone();
            }
            catch (JsonException) { /* not json */ }
        }
        return new ApiResult { Status = status, RawBody = body, Json = json };
    }
}

/// <summary>Thrown when an API call returns a non-success status.</summary>
public sealed class ApiException : Exception
{
    public int Status { get; }
    public string Body { get; }
    public ApiException(int status, string method, string url, string body)
        : base($"{method} {url} -> HTTP {status}: {Trim(body)}")
    {
        Status = status;
        Body = body;
    }
    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;
}

/// <summary>Login failed (bad credentials, etc.).</summary>
public sealed class AuthException : Exception
{
    public AuthException(string message) : base(message) { }
}

/// <summary>The account needs a 2FA code; call VerifyMfaAsync next.</summary>
public sealed class MfaRequiredException : Exception
{
    /// <summary>The IdP continuation URL returned by /idp/api/submit, if any.</summary>
    public string? ContinuationUrl { get; }
    public MfaRequiredException(string? continuationUrl)
        : base("Two-factor authentication code required.")
        => ContinuationUrl = continuationUrl;
}
