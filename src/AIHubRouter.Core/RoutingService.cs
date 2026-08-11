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

public sealed record LunaRouteResult(
    RouteDecision? Decision,
    RouteEvaluation? Evaluation,
    IReadOnlyList<long> SelectedKeyIds,
    IReadOnlyList<KeyRouteResult> KeyResults,
    int FilteredGroupCount,
    bool HealthAvailable,
    string HealthMessage);

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
    public LunaRouteResult? LunaRoute { get; init; }
    public ChannelReliabilityCycleResult? Reliability { get; init; }

    public int ChangedKeyCount =>
        KeyResults.Count(result => result.Changed && result.Success) +
        (LunaRoute?.KeyResults.Count(result => result.Changed && result.Success) ?? 0);

    public int FailedKeyCount =>
        KeyResults.Count(result => !result.Success) +
        (LunaRoute?.KeyResults.Count(result => !result.Success) ?? 0);
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

internal sealed record RouteLaneExecution(
    RouteDecisionResult DecisionResult,
    IReadOnlyList<KeyRouteResult> KeyResults,
    IReadOnlyList<ApiKeyInfo> UpdatedKeys);

public sealed class RoutingService : IDisposable
{
    private readonly PersistentAppSettings _settings;
    private readonly IRouteStateStore _stateStore;
    private readonly IAIHubClientFactory _clientFactory;
    private readonly ICloudflareChallengeSolver? _cloudflareChallengeSolver;
    private readonly Func<PersistentCredentials, CancellationToken, Task>? _persistCredentials;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly ProviderSeriesCache _providerSeriesCache;
    private readonly IChannelQuarantineStore _channelQuarantineStore;
    private readonly ChannelReliabilityMonitor? _reliabilityMonitor;
    private readonly bool _runReliabilityDuringRouting;
    private PersistentCredentials _credentials;
    private AuthSession? _currentSession;
    private IAIHubApiClient? _sessionClient;
    private IAIHubApiClient? _authenticatedClient;
    private string? _authenticatedClientToken;
    private IReadOnlyList<GroupInfo> _cachedGroups = [];
    private IReadOnlyDictionary<long, double> _cachedRates = new Dictionary<long, double>();
    private IReadOnlyDictionary<long, double> _cachedCacheHitRates = new Dictionary<long, double>();
    private IReadOnlyDictionary<long, IReadOnlyDictionary<string, string>> _cachedModelHealthByGroup =
        new Dictionary<long, IReadOnlyDictionary<string, string>>();
    private bool _modelHealthLoaded;
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
        ICloudflareChallengeSolver? cloudflareChallengeSolver = null,
        IChannelQuarantineStore? channelQuarantineStore = null,
        IChannelReliabilityDetector? reliabilityDetector = null,
        bool runReliabilityDuringRouting = true,
        ChannelReliabilityLedger? reliabilityLedger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _clientFactory = clientFactory ?? new AIHubClientFactory();
        _cloudflareChallengeSolver = cloudflareChallengeSolver;
        _persistCredentials = persistCredentials;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _providerSeriesCache = new ProviderSeriesCache(settings);
        _channelQuarantineStore = channelQuarantineStore ??
            new JsonChannelQuarantineStore(AppPaths.GetConfigurationDirectory());
        _runReliabilityDuringRouting = runReliabilityDuringRouting;
        if (settings.ReliabilityDetectionEnabled)
        {
            var workerPath = Path.GetFullPath(settings.DetectorWorkerPath);
            var workerDirectory = Path.GetDirectoryName(workerPath);
            _reliabilityMonitor = new ChannelReliabilityMonitor(
                settings,
                credentials,
                reliabilityDetector ?? new ProcessChannelReliabilityDetector(
                    settings.DetectorPythonCommand,
                    workerPath,
                    settings.DetectorPreset,
                    workingDirectory: workerDirectory),
                _channelQuarantineStore,
                _utcNow,
                ledger: reliabilityLedger);
        }
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
        IReadOnlyCollection<long>? selectedKeyIds = null,
        IReadOnlyCollection<long>? selectedLunaKeyIds = null)
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
                    selectedKeyIds,
                    selectedLunaKeyIds);
            }
            catch (AIHubApiException exception)
                when (attempt == 0 && exception.IsAuthenticationFailure && CanRenewAutomatically())
            {
                InvalidateSession();
            }
        }

        throw new InvalidOperationException("认证重试未返回结果。" );
    }

    public async Task<ChannelReliabilityCycleResult> RunReliabilityOnceAsync(
        bool dryRun = false,
        bool force = false,
        CancellationToken cancellationToken = default,
        ChannelReliabilityTrigger trigger = ChannelReliabilityTrigger.Scheduled,
        IReadOnlyCollection<long>? selectedKeyIds = null,
        IReadOnlyCollection<long>? selectedLunaKeyIds = null)
    {
        if (_reliabilityMonitor is null)
        {
            return BuildDisabledReliabilityCycleResult([], _utcNow());
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var client = await GetAuthenticatedClientAsync(
                forceRenew: attempt > 0,
                cancellationToken);
            try
            {
                await RefreshAccountDataAsync(
                    client,
                    _utcNow(),
                    forceRefresh: true,
                    cancellationToken,
                    requireLunaHealth: true);
                var primaryKeys = ResolveSelectedKeys(_cachedKeys, selectedKeyIds);
                var lunaKeys = ResolveSelectedKeys(
                    _cachedKeys,
                    selectedLunaKeyIds ?? _settings.LunaSelectedKeyIds);
                var reliabilityKeys = primaryKeys
                    .Concat(lunaKeys)
                    .GroupBy(key => key.Id)
                    .Select(group => group.First())
                    .ToArray();
                return await _reliabilityMonitor.CheckAsync(
                    reliabilityKeys,
                    _cachedModelHealthByGroup,
                    _cachedGroups,
                    dryRun,
                    force,
                    currentKeyResolver: keyId => _cachedKeys.FirstOrDefault(key => key.Id == keyId),
                    trigger: trigger,
                    cancellationToken: cancellationToken);
            }
            catch (AIHubApiException exception)
                when (attempt == 0 && exception.IsAuthenticationFailure && CanRenewAutomatically())
            {
                InvalidateSession();
            }
        }

        throw new InvalidOperationException("认证重试未返回可靠性检测结果。" );
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

    public ChannelReliabilityRuntimeSnapshot? ReliabilityRuntimeSnapshot =>
        _reliabilityMonitor?.RuntimeSnapshot;

    private async Task<RoutingCycleResult> RunCoreAsync(
        IAIHubApiClient client,
        bool dryRun,
        bool forceAccountRefresh,
        CancellationToken cancellationToken,
        IReadOnlyCollection<long>? selectedKeyIds,
        IReadOnlyCollection<long>? selectedLunaKeyIds)
    {
        var now = _utcNow();
        var policy = _settings.CreatePolicy();
        var requestedLunaKeyIds = selectedLunaKeyIds ?? _settings.LunaSelectedKeyIds;
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
        await RefreshAccountDataAsync(
            client,
            now,
            forceAccountRefresh,
            cancellationToken,
            requestedLunaKeyIds.Count > 0 || _settings.ReliabilityDetectionEnabled);

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

        var selectedLunaKeys = ResolveSelectedKeys(_cachedKeys, requestedLunaKeyIds);
        var lunaConfigured = requestedLunaKeyIds.Count > 0;
        var overlappingKeyIds = selectedKeys
            .Select(key => key.Id)
            .Intersect(selectedLunaKeys.Select(key => key.Id))
            .Order()
            .ToArray();
        if (overlappingKeyIds.Length > 0)
        {
            throw new InvalidOperationException(
                $"主路由与 Luna 路由不能选择同一 Key：{string.Join(", ", overlappingKeyIds)}。" );
        }

        var reliabilityKeys = selectedKeys
            .Concat(selectedLunaKeys)
            .GroupBy(key => key.Id)
            .Select(group => group.First())
            .ToArray();
        var reliability = _reliabilityMonitor is null || !_runReliabilityDuringRouting
            ? BuildDisabledReliabilityCycleResult(reliabilityKeys, now)
            : await _reliabilityMonitor.CheckAsync(
                reliabilityKeys,
                _cachedModelHealthByGroup,
                _cachedGroups,
                dryRun,
                force: forceAccountRefresh,
                currentKeyResolver: keyId => _cachedKeys.FirstOrDefault(key => key.Id == keyId),
                cancellationToken: cancellationToken);
        var reliabilityExcludedGroupIds = reliability.ExcludedGroupIds.ToHashSet();
        var state = _stateStore.Load();
        var evaluation = RoutingEngine.Evaluate(
            providers,
            _cachedGroups,
            _cachedRates,
            policy,
            now,
            providerSeries.Page?.Groups,
            reliabilityExcludedGroupIds);
        var providerSeriesStatus = ResolveProviderSeriesStatus(
            providerSeries.Status,
            evaluation);
        var decisionResult = RouteDecisionEngine.Decide(
            evaluation,
            state,
            policy,
            now,
            ResolveObservedGroup(selectedKeys));

        var primaryExecutionTask = ExecuteRouteLaneAsync(
            client,
            selectedKeys,
            decisionResult,
            dryRun,
            cancellationToken);
        Task<RouteLaneExecution>? lunaExecutionTask = null;
        LunaRouteResult? lunaRoute = null;
        var lunaHealth = ResolveLunaHealth();
        if (lunaConfigured)
        {
            if (selectedLunaKeys.Count == 0)
            {
                lunaRoute = new LunaRouteResult(
                    null,
                    null,
                    [],
                    [],
                    0,
                    false,
                    "没有选中的 active Luna API Key，已跳过 Luna 自动路由。" );
            }
            else if (!lunaHealth.Available)
            {
                lunaRoute = new LunaRouteResult(
                    null,
                    null,
                    selectedLunaKeys.Select(key => key.Id).ToArray(),
                    [],
                    0,
                    false,
                    lunaHealth.Message);
            }
            else
            {
                var lunaEvaluation = RoutingEngine.Evaluate(
                    providers,
                    _cachedGroups,
                    _cachedRates,
                    policy,
                    now,
                    providerSeries.Page?.Groups,
                    lunaHealth.FailedGroupIds
                        .Concat(reliabilityExcludedGroupIds)
                        .ToHashSet());
                var lunaState = new RouteState { CurrentGroupId = state.LunaCurrentGroupId };
                var lunaDecisionResult = RouteDecisionEngine.Decide(
                    lunaEvaluation,
                    lunaState,
                    policy,
                    now,
                    ResolveObservedGroup(selectedLunaKeys));
                lunaRoute = new LunaRouteResult(
                    lunaDecisionResult.Decision,
                    lunaEvaluation,
                    selectedLunaKeys.Select(key => key.Id).ToArray(),
                    [],
                    lunaHealth.FailedGroupIds
                        .Union(reliabilityExcludedGroupIds)
                        .Count(),
                    true,
                    lunaHealth.Message);
                lunaExecutionTask = ExecuteRouteLaneAsync(
                    client,
                    selectedLunaKeys,
                    lunaDecisionResult,
                    dryRun,
                    cancellationToken);
            }
        }

        if (lunaExecutionTask is null)
        {
            await primaryExecutionTask;
        }
        else
        {
            await Task.WhenAll(primaryExecutionTask, lunaExecutionTask);
        }

        var primaryExecution = await primaryExecutionTask;
        var lunaExecution = lunaExecutionTask is null
            ? null
            : await lunaExecutionTask;
        ReplaceCachedKeys(primaryExecution.UpdatedKeys.Concat(
            lunaExecution?.UpdatedKeys ?? Array.Empty<ApiKeyInfo>()));

        var routedKeyIds = primaryExecution.KeyResults
            .Concat(lunaExecution?.KeyResults ?? [])
            .Where(result => result.Success)
            .Select(result => result.KeyId)
            .ToHashSet();
        var groupChanged = reliabilityKeys.Any(previous =>
            routedKeyIds.Contains(previous.Id) &&
            _cachedKeys.FirstOrDefault(current => current.Id == previous.Id)?.GroupId != previous.GroupId);
        if (groupChanged && _reliabilityMonitor is not null && _runReliabilityDuringRouting)
        {
            var currentReliabilityKeys = reliabilityKeys
                .Select(previous => _cachedKeys.FirstOrDefault(current => current.Id == previous.Id) ?? previous)
                .ToArray();
            reliability = await _reliabilityMonitor.CheckAsync(
                currentReliabilityKeys,
                _cachedModelHealthByGroup,
                _cachedGroups,
                dryRun,
                force: false,
                currentKeyResolver: keyId => _cachedKeys.FirstOrDefault(key => key.Id == keyId),
                trigger: ChannelReliabilityTrigger.KeyGroupChanged,
                cancellationToken: cancellationToken);
        }

        if (!dryRun)
        {
            var nextState = primaryExecution.KeyResults.Any(result => !result.Success)
                ? primaryExecution.DecisionResult.NextState with { CurrentGroupId = null }
                : primaryExecution.DecisionResult.NextState;
            if (lunaExecution is not null)
            {
                var lunaNextState = lunaExecution.KeyResults.Any(result => !result.Success)
                    ? lunaExecution.DecisionResult.NextState with { CurrentGroupId = null }
                    : lunaExecution.DecisionResult.NextState;
                nextState = nextState with { LunaCurrentGroupId = lunaNextState.CurrentGroupId };
            }

            _stateStore.Save(nextState);
        }

        if (lunaExecution is not null && lunaRoute is not null)
        {
            lunaRoute = lunaRoute with { KeyResults = lunaExecution.KeyResults };
        }

        return new RoutingCycleResult(
            decisionResult.Decision,
            evaluation,
            providers,
            _cachedGroups,
            _cachedRates,
            _cachedKeys,
            selectedKeys.Select(key => key.Id).ToArray(),
            primaryExecution.KeyResults,
            providerSeries.Page?.Groups ?? new Dictionary<long, ProviderSeriesMetrics>(),
            providerSeriesStatus,
            _cacheHitRateStatus,
            dryRun,
            _utcNow())
        {
            LunaRoute = lunaRoute,
            Reliability = reliability
        };
    }

    private async Task<RouteLaneExecution> ExecuteRouteLaneAsync(
        IAIHubApiClient client,
        IReadOnlyList<ApiKeyInfo> selectedKeys,
        RouteDecisionResult decisionResult,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var keyResults = new List<KeyRouteResult>();
        var updatedKeys = new List<ApiKeyInfo>();
        if (!decisionResult.Decision.ShouldSwitch || decisionResult.Decision.Target is not { } target)
        {
            return new RouteLaneExecution(decisionResult, keyResults, updatedKeys);
        }

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
                updatedKeys.Add(updated);
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

        return new RouteLaneExecution(decisionResult, keyResults, updatedKeys);
    }

    private ChannelReliabilityCycleResult BuildDisabledReliabilityCycleResult(
        IReadOnlyList<ApiKeyInfo> selectedKeys,
        DateTimeOffset now)
    {
        var snapshot = new ChannelQuarantineSnapshot
        {
            CapturedAt = now.ToUniversalTime(),
            Records = _channelQuarantineStore.LoadLatest()
        };
        return new ChannelReliabilityCycleResult
        {
            Enabled = false,
            StartedAt = now.ToUniversalTime(),
            CompletedAt = now.ToUniversalTime(),
            Keys = selectedKeys
                .Select(key => new ChannelReliabilityKeySummary
                {
                    KeyId = key.Id,
                    KeyName = key.Name,
                    GroupId = key.GroupId,
                    Status = snapshot.IsActive(key.GroupId ?? 0, now)
                        ? ChannelReliabilityStatus.Quarantined
                        : ChannelReliabilityStatus.Unconfigured,
                    QuarantinedUntil = key.GroupId is { } groupId
                        ? snapshot.Records.FirstOrDefault(record =>
                            record.GroupId == groupId && record.IsActiveAt(now))?.ExpiresAt
                        : null
                })
                .ToArray(),
            Groups = snapshot.Records
                .Where(record => record.IsActiveAt(now))
                .GroupBy(record => record.GroupId)
                .Select(group => new ChannelReliabilityGroupSummary
                {
                    GroupId = group.Key,
                    Status = ChannelReliabilityStatus.Quarantined,
                    Verdict = group.First().Verdict,
                    SourceKeyId = group.First().SourceKeyId,
                    QuarantinedUntil = group.First().ExpiresAt
                })
                .OrderBy(group => group.GroupId)
                .ToArray(),
            Quarantine = snapshot
        };
    }

    private (bool Available, IReadOnlySet<long> FailedGroupIds, string Message) ResolveLunaHealth()
    {
        var statuses = _cachedModelHealthByGroup
            .SelectMany(pair => pair.Value
                .Where(model => model.Key.Equals("luna", StringComparison.OrdinalIgnoreCase))
                .Select(model => (pair.Key, model.Value)))
            .ToArray();
        if (statuses.Length == 0)
        {
            return (false, new HashSet<long>(), "供应商没有可用的 Luna 健康数据，已跳过 Luna 自动路由。" );
        }

        var failedGroupIds = statuses
            .Where(status => status.Value.Equals("failed", StringComparison.OrdinalIgnoreCase))
            .Select(status => status.Key)
            .ToHashSet();
        return (
            true,
            failedGroupIds,
            failedGroupIds.Count == 0
                ? "Luna 健康数据可用，未发现失败供应商。"
                : $"Luna 健康数据可用，已排除 {failedGroupIds.Count} 个失败供应商分组。" );
    }

    private void ReplaceCachedKeys(IEnumerable<ApiKeyInfo> updatedKeys)
    {
        var updates = updatedKeys
            .GroupBy(key => key.Id)
            .ToDictionary(group => group.Key, group => group.Last());
        if (updates.Count == 0)
        {
            return;
        }

        _cachedKeys = _cachedKeys
            .Select(key => updates.TryGetValue(key.Id, out var updated) ? updated : key)
            .ToArray();
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

        if (_channelQuarantineStore.GetActive(now).Any(record => record.GroupId == groupId))
        {
            throw new InvalidOperationException("所选分组处于可靠性隔离期，暂不能手动路由。" );
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
        var nextState = _stateStore.Load() with
        {
            CurrentGroupId = keyResults.Any(result => !result.Success)
                ? null
                : targetGroup.Id
        };
        _stateStore.Save(nextState);

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
            _stateStore.Save(_stateStore.Load() with { CurrentGroupId = null });
        }
    }

    private async Task RefreshAccountDataAsync(
        IAIHubApiClient client,
        DateTimeOffset now,
        bool forceRefresh,
        CancellationToken cancellationToken,
        bool requireLunaHealth = false)
    {
        if (!forceRefresh && now < _accountCacheExpiresAt && _cachedKeys.Count > 0 &&
            (!requireLunaHealth || _modelHealthLoaded))
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
        var shouldLoadProviderReferences = _settings.ProviderSeriesWeight > 0 ||
            requireLunaHealth ||
            _settings.ReliabilityDetectionEnabled;
        var cacheHitRatesTask = shouldLoadProviderReferences
            ? LoadProviderCacheHitRatesAsync(
                client,
                _settings.ProviderSeriesTimezone,
                cancellationToken)
            : Task.FromResult<(
                IReadOnlyDictionary<long, double> Rates,
                IReadOnlyDictionary<long, IReadOnlyDictionary<string, string>> ModelHealthByGroup,
                ProviderCacheHitRateLoadStatus Status)>(
                (new Dictionary<long, double>(),
                    new Dictionary<long, IReadOnlyDictionary<string, string>>(),
                    ProviderCacheHitRateLoadStatus.Disabled));
        await Task.WhenAll(groupsTask, ratesTask, keysTask, cacheHitRatesTask);
        _cachedGroups = await groupsTask;
        _cachedRates = await ratesTask;
        _cachedKeys = await keysTask;
        var cacheHitRates = await cacheHitRatesTask;
        _cachedCacheHitRates = _settings.ProviderSeriesWeight > 0
            ? cacheHitRates.Rates
            : new Dictionary<long, double>();
        if (shouldLoadProviderReferences)
        {
            _cachedModelHealthByGroup = cacheHitRates.ModelHealthByGroup;
            _modelHealthLoaded = true;
        }
        else
        {
            _cachedModelHealthByGroup = new Dictionary<long, IReadOnlyDictionary<string, string>>();
            _modelHealthLoaded = false;
        }
        _cacheHitRateStatus = _settings.ProviderSeriesWeight > 0
            ? cacheHitRates.Status
            : ProviderCacheHitRateLoadStatus.Disabled;
        _accountCacheExpiresAt = now.AddSeconds(Math.Clamp(_settings.AccountCacheSeconds, 30, 3600));
    }

    private static async Task<(
        IReadOnlyDictionary<long, double> Rates,
        IReadOnlyDictionary<long, IReadOnlyDictionary<string, string>> ModelHealthByGroup,
        ProviderCacheHitRateLoadStatus Status)> LoadProviderCacheHitRatesAsync(
        IAIHubApiClient client,
        string timezone,
        CancellationToken cancellationToken)
    {
        try
        {
            var page = await client.GetProviderCacheHitRatesAsync(timezone, cancellationToken);
            return (
                page.Groups,
                page.ModelHealthByGroup,
                page.Groups.Count == 0
                    ? ProviderCacheHitRateLoadStatus.Unavailable(
                        "供应商缓存命中率没有有效样本，已沿用基础评分。")
                    : ProviderCacheHitRateLoadStatus.Live);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AIHubApiException exception) when (exception.IsAuthenticationFailure)
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
            return (
                new Dictionary<long, double>(),
                new Dictionary<long, IReadOnlyDictionary<string, string>>(),
                ProviderCacheHitRateLoadStatus.Unavailable(message));
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
            UserAgent = _credentials.UserAgent,
            DetectorApiKeys = _credentials.DetectorApiKeys
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
        _reliabilityMonitor?.Dispose();
        _authenticatedClient?.Dispose();
        _sessionClient?.Dispose();
    }
}
