using System.Net;

namespace AIHubRouter.Core;

public sealed class AIHubApiException : Exception
{
    public AIHubApiException(string message, HttpStatusCode? statusCode = null, string? apiCode = null)
        : base(message)
    {
        StatusCode = statusCode;
        ApiCode = apiCode;
    }

    public HttpStatusCode? StatusCode { get; }

    public string? ApiCode { get; }

    public bool IsAuthenticationFailure =>
        StatusCode == HttpStatusCode.Unauthorized || IsAuthenticationApiCode(ApiCode);

    public bool IsRefreshRejection =>
        StatusCode == HttpStatusCode.BadRequest || IsAuthenticationFailure;

    private static bool IsAuthenticationApiCode(string? apiCode)
    {
        if (string.IsNullOrWhiteSpace(apiCode))
        {
            return false;
        }

        return apiCode.Trim().Replace('-', '_').ToLowerInvariant() switch
        {
            "401" or
            "unauthorized" or
            "invalid_grant" or
            "invalid_token" or
            "token_invalid" or
            "token_expired" or
            "expired_token" or
            "invalid_access_token" or
            "invalid_refresh_token" or
            "refresh_token_invalid" or
            "refresh_token_expired" => true,
            _ => false
        };
    }
}
