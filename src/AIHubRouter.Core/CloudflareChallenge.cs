using System.Net;

namespace AIHubRouter.Core;

public enum CloudflareChallengeKind
{
    JsChallenge,
    InteractiveChallenge
}

public sealed class CloudflareChallengeException : AIHubApiException
{
    public CloudflareChallengeException(
        Uri origin,
        CloudflareChallengeKind challengeKind,
        string message)
        : base(message, HttpStatusCode.Forbidden)
    {
        Origin = origin;
        ChallengeKind = challengeKind;
    }

    public Uri Origin { get; }

    public CloudflareChallengeKind ChallengeKind { get; }
}

public sealed record CloudflareChallengeSolution(
    string UserAgent,
    IReadOnlyDictionary<string, string> Cookies);

public interface ICloudflareChallengeSolver : IDisposable
{
    Task<CloudflareChallengeSolution?> SolveAsync(
        Uri origin,
        CancellationToken cancellationToken);
}

public static class CloudflareChallengeDetector
{
    private static readonly string[] InteractiveMarkers =
    [
        "verify you are human",
        "确认您是真人",
        "我不是机器人",
        "cf-chl-widget",
        "turnstile",
        "managed challenge"
    ];

    private static readonly string[] ChallengeMarkers =
    [
        "just a moment",
        "attention required!",
        "challenge-platform",
        "cdn-cgi/challenge-platform",
        "__cf_chl",
        "cf-chl-",
        "cf-mitigated",
        "cf_clearance",
        "enable javascript and cookies to continue"
    ];

    public static bool IsCloudflareChallenge(HttpResponseMessage response, string body)
    {
        return TryDetect(response, body, out _);
    }

    public static bool TryDetect(
        HttpResponseMessage response,
        string? body,
        out CloudflareChallengeKind challengeKind)
    {
        challengeKind = CloudflareChallengeKind.JsChallenge;
        var bodyText = body ?? string.Empty;
        if (LooksLikeJson(bodyText))
        {
            return false;
        }

        if (InteractiveMarkers.Any(marker => bodyText.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            challengeKind = CloudflareChallengeKind.InteractiveChallenge;
            return true;
        }

        if (ChallengeMarkers.Any(marker => bodyText.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var statusCode = (int)response.StatusCode;
        var isErrorStatus = statusCode is 403 or 429 or 503;
        var isHtml = bodyText.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
            bodyText.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase);
        return isErrorStatus && isHtml && HasCloudflareHeaders(response);
    }

    private static bool LooksLikeJson(string body)
    {
        foreach (var character in body.AsSpan().TrimStart())
        {
            return character is '{' or '[';
        }

        return false;
    }

    private static bool HasCloudflareHeaders(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Server", out var serverValues) &&
            serverValues.Any(value => value.Contains("cloudflare", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return response.Headers.Contains("CF-Ray");
    }
}