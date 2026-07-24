using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace AIHubRouter.Web;

public sealed class WebSessionManager(string password)
{
    private const string CookieName = "aihub_web_session";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);
    private readonly byte[] _passwordHash = SHA256.HashData(Encoding.UTF8.GetBytes(password));
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new(StringComparer.Ordinal);

    public bool TryLogin(string? candidate, HttpContext context)
    {
        var candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate ?? string.Empty));
        var valid = CryptographicOperations.FixedTimeEquals(candidateHash, _passwordHash);
        CryptographicOperations.ZeroMemory(candidateHash);
        if (!valid)
        {
            return false;
        }

        PruneExpired();
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        _sessions[token] = DateTimeOffset.UtcNow.Add(SessionLifetime);
        context.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            MaxAge = SessionLifetime,
            Path = "/"
        });
        return true;
    }

    public bool IsAuthenticated(HttpRequest request)
    {
        if (!request.Cookies.TryGetValue(CookieName, out var token) ||
            !_sessions.TryGetValue(token, out var expiresAt))
        {
            return false;
        }

        if (expiresAt <= DateTimeOffset.UtcNow)
        {
            _sessions.TryRemove(token, out _);
            return false;
        }

        _sessions[token] = DateTimeOffset.UtcNow.Add(SessionLifetime);
        return true;
    }

    public void Logout(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue(CookieName, out var token))
        {
            _sessions.TryRemove(token, out _);
        }

        context.Response.Cookies.Delete(CookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/"
        });
    }

    private void PruneExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var session in _sessions.Where(item => item.Value <= now))
        {
            _sessions.TryRemove(session.Key, out _);
        }
    }
}
