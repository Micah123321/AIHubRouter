namespace AIHubRouter.Core;

public static class CredentialParser
{
    private static readonly string[] TokenCookieNames = ["auth_token", "access_token", "token"];

    public static string NormalizeBearerToken(string? input)
    {
        var value = NormalizeSingleLine(input);
        if (value.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
        {
            value = value["Authorization:".Length..].Trim();
        }

        if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            value = value["Bearer ".Length..].Trim();
        }

        return value;
    }

    public static string NormalizeCookie(string? input)
    {
        var value = NormalizeSingleLine(input);
        return value.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)
            ? value["Cookie:".Length..].Trim()
            : value;
    }

    public static string NormalizeUserAgent(string? input)
    {
        var value = NormalizeSingleLine(input);
        return value.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase)
            ? value["User-Agent:".Length..].Trim()
            : value;
    }

    public static string TryExtractTokenFromCookie(string? cookie)
    {
        var normalized = NormalizeCookie(cookie);
        foreach (var segment in normalized.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var name = segment[..separator].Trim();
            if (!TokenCookieNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            return Uri.UnescapeDataString(segment[(separator + 1)..].Trim());
        }

        return string.Empty;
    }

    private static string NormalizeSingleLine(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var value = input.Trim();
        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentException("认证请求头不能包含换行符。");
        }

        return value;
    }
}
