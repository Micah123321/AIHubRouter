namespace AIHubRouter.Core;

public interface IAIHubClientFactory
{
    IAIHubApiClient Create(
        string baseUrl,
        string? bearerToken,
        string? cookie,
        string? userAgent,
        bool allowInsecureLoopback,
        ICloudflareChallengeSolver? cloudflareChallengeSolver = null);
}

public sealed class AIHubClientFactory : IAIHubClientFactory
{
    public IAIHubApiClient Create(
        string baseUrl,
        string? bearerToken,
        string? cookie,
        string? userAgent,
        bool allowInsecureLoopback,
        ICloudflareChallengeSolver? cloudflareChallengeSolver = null)
    {
        return new AIHubClient(
            baseUrl,
            bearerToken,
            cookie,
            userAgent,
            allowInsecureLoopback: allowInsecureLoopback,
            cloudflareChallengeSolver: cloudflareChallengeSolver);
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
    RouteEvaluation Evaluation,
    IReadOnlyList<ProviderStatus> Providers,
    IReadOnlyList<GroupInfo> Groups,
    IReadOnlyDictionary<long, double> UserGroupRates,
    IReadOnlyList<ApiKeyInfo> Keys,
    IReadOnlyList<long> SelectedKeyIds,
    IReadOnlyList<KeyRouteResult> KeyResults,
    IReadOnlyDictionary<long, ProviderSeriesMetrics> ProviderSeriesMetrics,
    ProviderSeriesLoadStatus ProviderSeriesStatus,
    ProviderCacheHitRateLoadStatus ProviderCacheHitRateStatus,
    bool DryRun,
    DateTimeOffset CompletedAt)
{
    public int ChangedKeyCount => KeyResults.Count(result => result.Changed && result.Success);
    public int FailedKeyCount => KeyResults.Count(result => !result.Success);
}

public sealed record ManualRoutingResult(
    GroupInfo TargetGroup,
    IReadOnlyList<ApiKeyInfo> Keys,
    IReadOnlyList<long> SelectedKeyIds,
    IReadOnlyList<KeyRouteResult> KeyResults,
    DateTimeOffset CompletedAt)
{
    public int ChangedKeyCount => KeyResults.Count(result => result.Changed && result.Success);
    public int FailedKeyCount => KeyResults.Count(result => !result.Success);
}

internal sealed class ManualRoutingProgress
{
    private readonly Dictionary<long, KeyRouteResult> _results = [];

    public bool HasUpdateAttempt { get; private set; }

    public void MarkUpdateAttempt() => HasUpdateAttempt = true;

    public void Record(KeyRouteResult result)
    {
        if (_results.TryGetValue(result.KeyId, out var existing) &&
            existing.Success &&
            existing.Changed &&
            result.Success &&
            !result.Changed)
        {
            return;
        }

        if (_results.TryGetValue(result.KeyId, out existing) &&
            existing.Changed &&
            result.Success &&
            !result.Changed)
        {
            result = result with { Changed = true };
        }

        _results[result.KeyId] = result;
    }

    public IReadOnlyList<KeyRouteResult> ResultsFor(IReadOnlyList<ApiKeyInfo> keys)
    {
        return keys.Select(key => _results.TryGetValue(key.Id, out var result)
            ? result
            : new KeyRouteResult(key.Id, key.Name, false, true, null))
            .ToArray();
    }
}

public sealed class RoutingService : IDisposable
{
    private readonly PersistentAppSettings _settings;
    private readonly IRouteStateStore _stateStore;
    private readonly IAIHubClientFactory _clientFactory;
    private readonly ICloudflareChallengeSolver? _cloudflareChallengeSolver;
    private readonly Func<PersistentCredentials, CancellationToken, Task>? _persistCredentials;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly ProviderSeriesCache _providerSeriesCache;
    private PersistentCredentials _credentials;
    private AuthSession? _currentSession;
    private IAIHubApiClient? _sessionClient;
    private IAIHubApiClient? _authenticatedClient;
    private string? _authenticatedClientToken;
    private IReadOnlyList<GroupInfo> _cachedGroups = [];
    private IReadOnlyDictionary<long, double> _cachedRates = new Dictionary<long, double>();
    private IReadOnlyDictionary<long, double> _cachedCacheHitRates = new Dictionary<long, double>();
    private IReadOnlyList<ApiKeyInfo> _cachedKeys = [];
    private ProviderCacheHitRateLoadStatus _cacheHitRateStatus;
    private DateTimeOffset _accountCacheExpiresAt = DateTimeOffset.MinValue;

    public RoutingService(
        PersistentAppSettings settings,
        PersistentCredentials credentials,
        IRouteStateStore stateStore,
        IAIHubClientFactory? clientFactory = null,
        Func<PersistentCredentials, CancellationToken, Task>? persistCredentials = null,
        Func<DateTimeOffset>? utcNow = null,
        ICloudflareChallengeSolver? cloudflareChallengeSolver = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _clientFactory = clientFactory ?? new AIHubClientFactory();
        _cloudflareChallengeSolver = cloudflareChallengeSolver;
        _persistCredentials = persistCredentials;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _providerSeriesCache = new ProviderSeriesCache(settings);
        _cacheHitRateStatus = settings.ProviderSeriesWeight > 0
            ? ProviderCacheHitRateLoadStatus.Unavailable("供应商缓存命中率尚未加载。")
            : ProviderCacheHitRateLoadStatus.Disabled;

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
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<long>? selectedKeyIds = null)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var client = await GetAuthenticatedClientAsync(
                forceRenew: attempt > 0,
                cancellationToken);
            try
            {
                return await RunCoreAsync(
                    client,
                    dryRun,
                    forceAccountRefresh,
                    cancellationToken,
                    selectedKeyIds);
            }
            catch (AIHubApiException exception)
                when (attempt == 0 && exception.IsAuthenticationFailure && CanRenewAutomatically())
            {
                InvalidateSession();
            }
        }

        throw new InvalidOperationException("认证重试未返回结果。" );
    }

    public async Task<ManualRoutingResult> RouteManuallyAsync(
        long groupId,
        bool forceAccountRefresh = false,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<long>? selectedKeyIds = null)
    {
        if (groupId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(groupId));
        }

        var progress = new ManualRoutingProgress();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var client = await GetAuthenticatedClientAsync(
                    forceRenew: attempt > 0,
                    cancellationToken);
                return await RouteManuallyCoreAsync(
                    client,
                    groupId,
                    forceAccountRefresh,
                    cancellationToken,
                    progress,
                    selectedKeyIds);
            }
            catch (AIHubApiException exception)
                when (attempt == 0 && exception.IsAuthenticationFailure && CanRenewAutomatically())
            {
                InvalidateSession();
            }
            catch
            {
                ClearManualRouteStateAfterInterruptedUpdate(progress);
                throw;
            }
        }

        ClearManualRouteStateAfterInterruptedUpdate(progress);
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
        CancellationToken cancellationToken,
        IReadOnlyCollection<long>? selectedKeyIds)
    {
        var now = _utcNow();
        var policy = _settings.CreatePolicy();
        var usageStatsTask = client.GetGroupUsageStatsAsync(
            policy.Platform,
            GroupUsageEstimator.DefaultSampleLimit,
            cancellationToken);
        var providerSeriesTask = _providerSeriesCache.LoadAsync(
            client,
            policy,
            now,
            forceAccountRefresh,
            cancellationToken);
        await RefreshAccountDataAsync(client, now, forceAccountRefresh, cancellationToken);

        var usageStats = new[] { await usageStatsTask };
        var providerSeries = await providerSeriesTask;
        var providers = GroupUsageEstimator.Estimate(
            usageStats,
            now,
            policy.MaximumStatusAge,
            policy.MinimumConfidence);
        foreach (var provider in providers)
        {
            if (provider.GroupId is { } groupId &&
                _cachedCacheHitRates.TryGetValue(groupId, out var cacheHitRate))
            {
                provider.CacheHitRate = cacheHitRate;
            }
        }
        var selectedKeys = ResolveSelectedKeys(_cachedKeys, selectedKeyIds);
        if (selectedKeys.Count == 0)
        {
            throw new InvalidOperationException(
                "没有选中的 active API Key。请先配置 SelectedKeyIds，或在首次运行时保留一个 active Key。" );
        }

        var observedGroupId = ResolveObservedGroup(selectedKeys);
        var state = _stateStore.Load();
        var evaluation = RoutingEngine.Evaluate(
            providers,
            _cachedGroups,
            _cachedRates,
            policy,
            now,
            providerSeries.Page?.Groups);
        var providerSeriesStatus = ResolveProviderSeriesStatus(
            providerSeries.Status,
            evaluation);
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
                ? decisionResult.NextState with { CurrentGroupId = null }
                : decisionResult.NextState;
            _stateStore.Save(nextState);
        }

        return new RoutingCycleResult(
            decisionResult.Decision,
            evaluation,
            providers,
            _cachedGroups,
            _cachedRates,
            _cachedKeys,
            selectedKeys.Select(key => key.Id).ToArray(),
            keyResults,
            providerSeries.Page?.Groups ?? new Dictionary<long, ProviderSeriesMetrics>(),
            providerSeriesStatus,
            _cacheHitRateStatus,
            dryRun,
            _utcNow());
    }

    private static ProviderSeriesLoadStatus ResolveProviderSeriesStatus(
        ProviderSeriesLoadStatus loadStatus,
        RouteEvaluation evaluation)
    {
        if (!loadStatus.Available)
        {
            return loadStatus;
        }

        if (evaluation.Baseline is { } baseline &&
            evaluation.ProviderSeriesScores.ContainsKey(baseline.Group.Id))
        {
            return loadStatus;
        }

        return new ProviderSeriesLoadStatus(
            false,
            loadStatus.FromCache,
            true,
            "供应商序列没有可比较的基准，已沿用基础评分。");
    }

    private async Task<ManualRoutingResult> RouteManuallyCoreAsync(
        IAIHubApiClient client,
        long groupId,
        bool forceAccountRefresh,
        CancellationToken cancellationToken,
        ManualRoutingProgress progress,
        IReadOnlyCollection<long>? selectedKeyIds)
    {
        var now = _utcNow();
        var policy = _settings.CreatePolicy();
        await RefreshAccountDataAsync(client, now, forceAccountRefresh, cancellationToken);

        if (_settings.BlacklistedGroupIds.Contains(groupId))
        {
            throw new InvalidOperationException("所选分组已加入黑名单。" );
        }

        var targetGroup = _cachedGroups.FirstOrDefault(group =>
            group.Id == groupId &&
            group.Status.Equals("active", StringComparison.OrdinalIgnoreCase) &&
            group.Platform.Equals(_settings.Platform, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("所选方案不可用，或当前账号没有该分组权限。" );
        var usageStatsTask = client.GetGroupUsageStatsAsync(
            policy.Platform,
            GroupUsageEstimator.DefaultSampleLimit,
            cancellationToken);
        var targetMultiplier = _cachedRates.TryGetValue(targetGroup.Id, out var overriddenMultiplier)
            ? overriddenMultiplier
            : GroupUsageEstimator.Estimate(
                    [await usageStatsTask],
                    now,
                    policy.MaximumStatusAge,
                    policy.MinimumConfidence)
                .FirstOrDefault(provider => provider.GroupId == targetGroup.Id)
                ?.PriceMultiplier;
        if (targetMultiplier is not { } multiplier ||
            !double.IsFinite(multiplier) ||
            !RoutingEngine.IsWithinPriceRange(multiplier, policy))
        {
            throw new InvalidOperationException(
                $"所选分组不在允许价格范围 {policy.MinimumPriceMultiplier:0.####}-{policy.MaximumPriceMultiplier:0.####} 内，或当前无法确认其倍率。");
        }
        var selectedKeys = ResolveSelectedKeys(_cachedKeys, selectedKeyIds);
        if (selectedKeys.Count == 0)
        {
            throw new InvalidOperationException(
                "没有选中的 active API Key。请先选择至少一个可用 Key。" );
        }

        foreach (var key in selectedKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (key.GroupId == targetGroup.Id)
            {
                progress.Record(new KeyRouteResult(key.Id, key.Name, false, true, null));
                continue;
            }

            try
            {
                progress.MarkUpdateAttempt();
                var updated = await client.UpdateKeyGroupAsync(
                    key.Id,
                    targetGroup.Id,
                    cancellationToken);
                ReplaceCachedKey(updated);
                progress.Record(new KeyRouteResult(key.Id, key.Name, true, true, null));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (exception is AIHubApiException { IsAuthenticationFailure: true })
                {
                    throw;
                }

                progress.Record(new KeyRouteResult(
                    key.Id,
                    key.Name,
                    true,
                    false,
                    GetSafeErrorMessage(exception)));
            }
        }

        var keyResults = progress.ResultsFor(selectedKeys);
        _stateStore.Save(new RouteState
        {
            CurrentGroupId = keyResults.Any(result => !result.Success)
                ? null
                : targetGroup.Id
        });

        return new ManualRoutingResult(
            targetGroup,
            _cachedKeys,
            selectedKeys.Select(key => key.Id).ToArray(),
            keyResults,
            _utcNow());
    }

    private void ClearManualRouteStateAfterInterruptedUpdate(ManualRoutingProgress progress)
    {
        if (progress.HasUpdateAttempt)
        {
            _stateStore.Save(new RouteState { CurrentGroupId = null });
        }
    }

    private async Task RefreshAccountDataAsync(
        IAIHubApiClient client,
        DateTimeOffset now,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (!forceRefresh && now < _accountCacheExpiresAt && _cachedKeys.Count > 0)
        {
            if (_cacheHitRateStatus.Available)
            {
                _cacheHitRateStatus = _cacheHitRateStatus with { FromCache = true };
            }
            return;
        }

        var groupsTask = client.GetAvailableGroupsAsync(cancellationToken);
        var ratesTask = client.GetUserGroupRatesAsync(cancellationToken);
        var keysTask = client.GetAllKeysAsync(cancellationToken);
        var cacheHitRatesTask = _settings.ProviderSeriesWeight > 0
            ? LoadProviderCacheHitRatesAsync(
                client,
                _settings.ProviderSeriesTimezone,
                cancellationToken)
            : Task.FromResult<(IReadOnlyDictionary<long, double> Rates, ProviderCacheHitRateLoadStatus Status)>(
                (new Dictionary<long, double>(), ProviderCacheHitRateLoadStatus.Disabled));
        await Task.WhenAll(groupsTask, ratesTask, keysTask, cacheHitRatesTask);
        _cachedGroups = await groupsTask;
        _cachedRates = await ratesTask;
        _cachedKeys = await keysTask;
        var cacheHitRates = await cacheHitRatesTask;
        _cachedCacheHitRates = cacheHitRates.Rates;
        _cacheHitRateStatus = cacheHitRates.Status;
        _accountCacheExpiresAt = now.AddSeconds(Math.Clamp(_settings.AccountCacheSeconds, 30, 3600));
    }

    private static async Task<(
        IReadOnlyDictionary<long, double> Rates,
        ProviderCacheHitRateLoadStatus Status)> LoadProviderCacheHitRatesAsync(
        IAIHubApiClient client,
        string timezone,
        CancellationToken cancellationToken)
    {
        try
        {
            var page = await client.GetProviderCacheHitRatesAsync(timezone, cancellationToken);
            return page.Groups.Count == 0
                ? (new Dictionary<long, double>(), ProviderCacheHitRateLoadStatus.Unavailable(
                    "供应商缓存命中率没有有效样本，已沿用基础评分。"))
                : (page.Groups, ProviderCacheHitRateLoadStatus.Live);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is AIHubApiException or
                HttpRequestException or
                TaskCanceledException or
                InvalidDataException)
        {
            var message = exception switch
            {
                HttpRequestException => "供应商缓存命中率网络请求失败，已沿用基础评分。",
                TaskCanceledException => "供应商缓存命中率请求超时，已沿用基础评分。",
                AIHubApiException => "供应商缓存命中率接口返回错误，已沿用基础评分。",
                InvalidDataException => "供应商缓存命中率数据不可用，已沿用基础评分。",
                _ => "供应商缓存命中率加载失败，已沿用基础评分。"
            };
            return (new Dictionary<long, double>(), ProviderCacheHitRateLoadStatus.Unavailable(message));
        }
    }

    private IReadOnlyList<ApiKeyInfo> ResolveSelectedKeys(
        IReadOnlyList<ApiKeyInfo> keys,
        IReadOnlyCollection<long>? selectedKeyIds = null)
    {
        var selectedIds = selectedKeyIds ?? KeySelectionPolicy.Resolve(
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
            _settings.AllowInsecureLoopback,
            _cloudflareChallengeSolver);
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
            _settings.AllowInsecureLoopback,
            _cloudflareChallengeSolver);
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
