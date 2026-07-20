namespace AIHubRouter.Core;

public interface IAIHubClientFactory
{
    IAIHubApiClient Create(
        string baseUrl,
        string? bearerToken,
        string? cookie,
        string? userAgent,
        bool allowInsecureLoopback);
}

public sealed class AIHubClientFactory : IAIHubClientFactory
{
    public IAIHubApiClient Create(
        string baseUrl,
        string? bearerToken,
        string? cookie,
        string? userAgent,
        bool allowInsecureLoopback)
    {
        return new AIHubClient(
            baseUrl,
            bearerToken,
            cookie,
            userAgent,
            allowInsecureLoopback: allowInsecureLoopback);
    }
}

public sealed record KeyRouteResult(
    long KeyId,
    string KeyName,
    bool Changed,
    bool Success,
    string? Error);

public sealed record RoutingCycleResult(
    RouteDecision Decision,
    IReadOnlyList<ProviderStatus> Providers,
    IReadOnlyList<GroupInfo> Groups,
    IReadOnlyList<ApiKeyInfo> Keys,
    IReadOnlyList<long> SelectedKeyIds,
    IReadOnlyList<KeyRouteResult> KeyResults,
    bool DryRun,
    DateTimeOffset CompletedAt)
{
    public int ChangedKeyCount => KeyResults.Count(result => result.Changed && result.Success);
    public int FailedKeyCount => KeyResults.Count(result => !result.Success);
}

public sealed class RoutingService : IDisposable
{
    private readonly PersistentAppSettings _settings;
    private readonly IRouteStateStore _stateStore;
    private readonly IAIHubClientFactory _clientFactory;
    private readonly Func<PersistentCredentials, CancellationToken, Task>? _persistCredentials;
    private readonly Func<DateTimeOffset> _utcNow;
    private PersistentCredentials _credentials;
    private AuthSession? _currentSession;
    private IAIHubApiClient? _sessionClient;
    private IAIHubApiClient? _authenticatedClient;
    private string? _authenticatedClientToken;
    private IReadOnlyList<GroupInfo> _cachedGroups = [];
    private IReadOnlyDictionary<long, double> _cachedRates = new Dictionary<long, double>();
    private IReadOnlyList<ApiKeyInfo> _cachedKeys = [];
    private DateTimeOffset _accountCacheExpiresAt = DateTimeOffset.MinValue;

    public RoutingService(
        PersistentAppSettings settings,
        PersistentCredentials credentials,
        IRouteStateStore stateStore,
        IAIHubClientFactory? clientFactory = null,
        Func<PersistentCredentials, CancellationToken, Task>? persistCredentials = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _clientFactory = clientFactory ?? new AIHubClientFactory();
        _persistCredentials = persistCredentials;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

        if (!string.IsNullOrWhiteSpace(credentials.BearerToken) ||
            !string.IsNullOrWhiteSpace(credentials.RefreshToken))
        {
            _currentSession = new AuthSession(
                credentials.BearerToken,
                credentials.RefreshToken,
                credentials.AccessTokenExpiresAt ?? DateTimeOffset.MinValue);
        }
    }

    public async Task<RoutingCycleResult> RunOnceAsync(
        bool dryRun = false,
        bool forceAccountRefresh = false,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var client = await GetAuthenticatedClientAsync(
                forceRenew: attempt > 0,
                cancellationToken);
            try
            {
                return await RunCoreAsync(client, dryRun, forceAccountRefresh, cancellationToken);
            }
            catch (AIHubApiException exception)
                when (attempt == 0 && exception.IsAuthenticationFailure && CanRenewAutomatically())
            {
                InvalidateSession();
            }
        }

        throw new InvalidOperationException("认证重试未返回结果。" );
    }

    public void InvalidateAccountCache()
    {
        _accountCacheExpiresAt = DateTimeOffset.MinValue;
    }

    private async Task<RoutingCycleResult> RunCoreAsync(
        IAIHubApiClient client,
        bool dryRun,
        bool forceAccountRefresh,
        CancellationToken cancellationToken)
    {
        var now = _utcNow();
        var summaryTask = client.GetProviderSummaryAsync(cancellationToken);
        if (forceAccountRefresh || now >= _accountCacheExpiresAt || _cachedKeys.Count == 0)
        {
            var groupsTask = client.GetAvailableGroupsAsync(cancellationToken);
            var ratesTask = client.GetUserGroupRatesAsync(cancellationToken);
            var keysTask = client.GetAllKeysAsync(cancellationToken);
            await Task.WhenAll(groupsTask, ratesTask, keysTask);
            _cachedGroups = await groupsTask;
            _cachedRates = await ratesTask;
            _cachedKeys = await keysTask;
            _accountCacheExpiresAt = now.AddSeconds(Math.Clamp(_settings.AccountCacheSeconds, 30, 3600));
        }

        var summary = await summaryTask;
        var selectedKeys = ResolveSelectedKeys(_cachedKeys);
        if (selectedKeys.Count == 0)
        {
            throw new InvalidOperationException(
                "没有选中的 active API Key。请先配置 SelectedKeyIds，或在首次运行时保留一个 active Key。" );
        }

        var observedGroupId = ResolveObservedGroup(selectedKeys);
        var state = _stateStore.Load();
        var policy = _settings.CreatePolicy();
        var evaluation = RoutingEngine.Evaluate(
            summary.Apis,
            _cachedGroups,
            _cachedRates,
            policy,
            now);
        var decisionResult = RouteDecisionEngine.Decide(
            evaluation,
            state,
            policy,
            now,
            observedGroupId);
        var keyResults = new List<KeyRouteResult>();

        if (decisionResult.Decision.ShouldSwitch && decisionResult.Decision.Target is { } target)
        {
            foreach (var key in selectedKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (key.GroupId == target.Group.Id)
                {
                    keyResults.Add(new KeyRouteResult(key.Id, key.Name, false, true, null));
                    continue;
                }

                if (dryRun)
                {
                    keyResults.Add(new KeyRouteResult(key.Id, key.Name, true, true, null));
                    continue;
                }

                try
                {
                    var updated = await client.UpdateKeyGroupAsync(
                        key.Id,
                        target.Group.Id,
                        cancellationToken);
                    ReplaceCachedKey(updated);
                    keyResults.Add(new KeyRouteResult(key.Id, key.Name, true, true, null));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    if (exception is AIHubApiException { IsAuthenticationFailure: true })
                    {
                        throw;
                    }

                    keyResults.Add(new KeyRouteResult(
                        key.Id,
                        key.Name,
                        true,
                        false,
                        GetSafeErrorMessage(exception)));
                }
            }
        }

        if (!dryRun)
        {
            var nextState = keyResults.Any(result => !result.Success)
                ? decisionResult.NextState with
                {
                    CurrentGroupId = null,
                    PendingGroupId = null,
                    PendingConfirmationCount = 0,
                    LastSwitchAt = state.LastSwitchAt
                }
                : decisionResult.NextState;
            _stateStore.Save(nextState);
        }

        return new RoutingCycleResult(
            decisionResult.Decision,
            summary.Apis,
            _cachedGroups,
            _cachedKeys,
            selectedKeys.Select(key => key.Id).ToArray(),
            keyResults,
            dryRun,
            _utcNow());
    }

    private IReadOnlyList<ApiKeyInfo> ResolveSelectedKeys(IReadOnlyList<ApiKeyInfo> keys)
    {
        var selectedIds = KeySelectionPolicy.Resolve(
            _settings.KeySelectionInitialized,
            _settings.SelectedKeyIds,
            keys);
        var selected = selectedIds.ToHashSet();
        return keys
            .Where(key => selected.Contains(key.Id))
            .Where(key => key.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static long? ResolveObservedGroup(IReadOnlyList<ApiKeyInfo> keys)
    {
        var groups = keys
            .Select(key => key.GroupId)
            .Where(groupId => groupId is > 0)
            .Distinct()
            .ToArray();
        return groups.Length == 1 ? groups[0] : null;
    }

    private void ReplaceCachedKey(ApiKeyInfo updated)
    {
        _cachedKeys = _cachedKeys
            .Select(key => key.Id == updated.Id ? updated : key)
            .ToArray();
    }

    private async Task<IAIHubApiClient> GetAuthenticatedClientAsync(
        bool forceRenew,
        CancellationToken cancellationToken)
    {
        var loginCredentials = new LoginCredentials(_credentials.Email, _credentials.Password);
        var canCoordinate = loginCredentials.IsComplete ||
            !string.IsNullOrWhiteSpace(_currentSession?.RefreshToken);
        if (!canCoordinate)
        {
            if (string.IsNullOrWhiteSpace(_credentials.BearerToken) &&
                string.IsNullOrWhiteSpace(_credentials.Cookie))
            {
                throw new InvalidOperationException("缺少认证信息。请通过 stdin、环境变量或安全凭据存储提供。" );
            }

            return GetOrCreateAuthenticatedClient(_credentials.BearerToken);
        }

        if (forceRenew && _currentSession is not null)
        {
            _currentSession = _currentSession with { ExpiresAt = DateTimeOffset.MinValue };
        }

        _sessionClient ??= _clientFactory.Create(
            _settings.BaseUrl,
            null,
            _credentials.Cookie,
            _credentials.UserAgent,
            _settings.AllowInsecureLoopback);
        var coordinator = new SessionCoordinator(
            _sessionClient.RefreshSessionAsync,
            _sessionClient.LoginAsync,
            PersistSessionAsync,
            _utcNow);
        _currentSession = await coordinator.GetSessionAsync(
            _currentSession,
            loginCredentials,
            cancellationToken);
        return GetOrCreateAuthenticatedClient(_currentSession.AccessToken);
    }

    private IAIHubApiClient GetOrCreateAuthenticatedClient(string bearerToken)
    {
        if (_authenticatedClient is not null &&
            string.Equals(_authenticatedClientToken, bearerToken, StringComparison.Ordinal))
        {
            return _authenticatedClient;
        }

        _authenticatedClient?.Dispose();
        _authenticatedClient = _clientFactory.Create(
            _settings.BaseUrl,
            bearerToken,
            _credentials.Cookie,
            _credentials.UserAgent,
            _settings.AllowInsecureLoopback);
        _authenticatedClientToken = bearerToken;
        return _authenticatedClient;
    }

    private async Task PersistSessionAsync(AuthSession session, CancellationToken cancellationToken)
    {
        _currentSession = session;
        _credentials = new PersistentCredentials
        {
            Email = _credentials.Email,
            Password = _credentials.Password,
            BearerToken = session.AccessToken,
            RefreshToken = session.RefreshToken,
            AccessTokenExpiresAt = session.ExpiresAt,
            Cookie = _credentials.Cookie,
            UserAgent = _credentials.UserAgent
        };

        if (_persistCredentials is not null)
        {
            await _persistCredentials(_credentials, cancellationToken);
        }
    }

    private bool CanRenewAutomatically()
    {
        return new LoginCredentials(_credentials.Email, _credentials.Password).IsComplete ||
            !string.IsNullOrWhiteSpace(_currentSession?.RefreshToken);
    }

    private void InvalidateSession()
    {
        if (_currentSession is not null)
        {
            _currentSession = _currentSession with { ExpiresAt = DateTimeOffset.MinValue };
        }

        _authenticatedClient?.Dispose();
        _authenticatedClient = null;
        _authenticatedClientToken = null;
    }

    private static string GetSafeErrorMessage(Exception exception)
    {
        return exception switch
        {
            AIHubApiException apiException => apiException.Message,
            HttpRequestException => "网络连接失败。",
            TaskCanceledException => "请求超时。",
            _ => "路由请求失败。"
        };
    }

    public void Dispose()
    {
        _authenticatedClient?.Dispose();
        _sessionClient?.Dispose();
    }
}
