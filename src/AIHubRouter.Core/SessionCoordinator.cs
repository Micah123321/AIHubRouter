namespace AIHubRouter.Core;

public sealed class SessionCoordinator
{
    private static readonly TimeSpan RenewalMargin = TimeSpan.FromMinutes(2);
    private readonly Func<string, CancellationToken, Task<AuthSession>> _refreshSession;
    private readonly Func<LoginCredentials, CancellationToken, Task<AuthSession>> _login;
    private readonly Func<AuthSession, CancellationToken, Task> _persistSession;
    private readonly Func<DateTimeOffset> _utcNow;

    public SessionCoordinator(
        Func<string, CancellationToken, Task<AuthSession>> refreshSession,
        Func<LoginCredentials, CancellationToken, Task<AuthSession>> login,
        Func<AuthSession, CancellationToken, Task> persistSession,
        Func<DateTimeOffset>? utcNow = null)
    {
        _refreshSession = refreshSession ?? throw new ArgumentNullException(nameof(refreshSession));
        _login = login ?? throw new ArgumentNullException(nameof(login));
        _persistSession = persistSession ?? throw new ArgumentNullException(nameof(persistSession));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<AuthSession> GetSessionAsync(
        AuthSession? currentSession,
        LoginCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        if (currentSession?.IsUsable(_utcNow(), RenewalMargin) == true)
        {
            return currentSession;
        }

        if (!string.IsNullOrWhiteSpace(currentSession?.RefreshToken))
        {
            try
            {
                var refreshed = await _refreshSession(currentSession.RefreshToken, cancellationToken);
                await _persistSession(refreshed, cancellationToken);
                return refreshed;
            }
            catch (AIHubApiException exception) when (IsRefreshRejected(exception))
            {
                // A rejected refresh token can be recovered with stored login credentials.
            }
        }

        if (!credentials.IsComplete)
        {
            throw new InvalidOperationException("自动登录需要完整的邮箱和密码。");
        }

        var loggedIn = await _login(credentials, cancellationToken);
        await _persistSession(loggedIn, cancellationToken);
        return loggedIn;
    }

    private static bool IsRefreshRejected(AIHubApiException exception)
    {
        return exception.IsRefreshRejection;
    }
}
