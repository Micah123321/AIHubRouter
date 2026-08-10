using AIHubRouter.Core;

namespace AIHubRouter.Web;

public sealed class WebRouterCoordinator : BackgroundService
{
    private readonly AppSettingsStore _store = new();
    private readonly ILogger<WebRouterCoordinator> _logger;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _stateLock = new();
    private PersistentAppSettings _storedSettings;
    private PersistentCredentials _storedCredentials;
    private bool _storedCredentialsUnavailable;
    private PersistentAppSettings _settings;
    private PersistentCredentials _credentials;
    private RoutingService? _service;
    private ProfileLock? _profileLock;
    private RoutingCycleResult? _lastResult;
    private bool _showProviderSeriesStatus;
    private bool _isBusy;
    private string _status = "就绪";
    private string _statusKind = "neutral";
    private DateTimeOffset? _lastUpdatedAt;
    private DateTimeOffset _nextAutoRun = DateTimeOffset.MinValue;

    public WebRouterCoordinator(ILogger<WebRouterCoordinator> logger)
    {
        _logger = logger;
        var snapshot = _store.Load();
        _storedSettings = snapshot.Settings;
        _storedCredentials = snapshot.Credentials ?? new PersistentCredentials();
        _storedCredentialsUnavailable = snapshot.CredentialsUnavailable;
        _settings = ApplyEnvironmentSettings(_storedSettings);
        _credentials = ApplyEnvironmentCredentials(_storedCredentials);
    }

    public WebDashboard GetDashboard()
    {
        lock (_stateLock)
        {
            return BuildDashboard();
        }
    }

    public async Task<WebDashboard> SaveSettingsAsync(
        SettingsUpdateRequest request,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken);
        SetBusy(true);
        try
        {
            ValidateSettings(request);
            var selectedKeyIds = NormalizeIds(request.SelectedKeyIds);
            var lunaSelectedKeyIds = request.LunaSelectedKeyIds is null
                ? NormalizeIds(_settings.LunaSelectedKeyIds)
                : NormalizeIds(request.LunaSelectedKeyIds);
            var overlappingKeyIds = selectedKeyIds.Intersect(lunaSelectedKeyIds).ToArray();
            if (overlappingKeyIds.Length > 0)
            {
                throw new InvalidOperationException(
                    $"主路由与 Luna 路由不能选择同一 Key：{string.Join(", ", overlappingKeyIds)}。请取消其中一侧的选择后重试。");
            }
            if (lunaSelectedKeyIds.Length > 0 && selectedKeyIds.Length == 0)
            {
                throw new InvalidOperationException(
                    "Luna 路由不能脱离主路由单独运行，请先选择主路由 Key。" );
            }

            var detectorBindings = request.DetectorBindings is null
                ? _storedSettings.DetectorBindings
                : NormalizeDetectorBindings(request.DetectorBindings);
            var detectorApiKeys = new Dictionary<long, string>(_storedCredentials.DetectorApiKeys ?? []);
            if (request.DetectorApiKeys is not null)
            {
                foreach (var pair in request.DetectorApiKeys)
                {
                    if (pair.Key <= 0)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(pair.Value))
                    {
                        detectorApiKeys.Remove(pair.Key);
                    }
                    else
                    {
                        detectorApiKeys[pair.Key] = pair.Value;
                    }
                }
            }

            var oldSettings = _settings;
            var storedCredentials = _storedCredentials with
            {
                Email = HasEnvironmentVariable("AIHUB_EMAIL")
                    ? _storedCredentials.Email
                    : request.Email.Trim(),
                Password = request.ClearPassword
                    ? HasEnvironmentVariable("AIHUB_PASSWORD")
                        ? _storedCredentials.Password
                        : string.Empty
                    : request.Password is null
                        ? _storedCredentials.Password
                        : request.Password,
                BearerToken = request.ClearBearerToken
                    ? HasEnvironmentVariable("AIHUB_TOKEN")
                        ? _storedCredentials.BearerToken
                        : string.Empty
                    : request.BearerToken is null
                        ? _storedCredentials.BearerToken
                        : CredentialParser.NormalizeBearerToken(request.BearerToken)
            };
            var storedSettings = _storedSettings with
            {
                BaseUrl = HasEnvironmentVariable("AIHUB_BASE_URL")
                    ? _storedSettings.BaseUrl
                    : request.BaseUrl.Trim().TrimEnd('/'),
                RoutingMode = request.RoutingMode,
                GroupStickiness = request.GroupStickiness,
                MinimumPriceMultiplier = request.MinimumPriceMultiplier,
                MaximumPriceMultiplier = request.MaximumPriceMultiplier,
                ConfidenceImpact = request.ConfidenceImpact,
                MinimumConfidence = request.MinimumConfidence,
                ProviderSeriesWeight = request.ProviderSeriesWeight ?? _settings.ProviderSeriesWeight,
                ProviderSeriesCacheSeconds =
                    request.ProviderSeriesCacheSeconds ?? _settings.ProviderSeriesCacheSeconds,
                ProviderSeriesRange =
                    request.ProviderSeriesRange?.Trim() ?? _settings.ProviderSeriesRange,
                ProviderSeriesTimezone =
                    request.ProviderSeriesTimezone?.Trim() ?? _settings.ProviderSeriesTimezone,
                PollingIntervalSeconds = Math.Clamp(request.PollingIntervalSeconds, 30, 3600),
                PersistCredentials = request.PersistCredentials,
                ThemeMode = request.ThemeMode,
                KeySelectionInitialized = _lastResult?.Keys.Count > 0
                    ? true
                    : selectedKeyIds.Length > 0 ||
                        lunaSelectedKeyIds.Length > 0 ||
                        (_settings.KeySelectionInitialized && _settings.SelectedKeyIds.Length > 0),
                SelectedKeyIds = selectedKeyIds,
                LunaSelectedKeyIds = lunaSelectedKeyIds,
                BlacklistedGroupIds = request.BlacklistedGroupIds.Where(id => id > 0).Distinct().Order().ToArray(),
                ReliabilityDetectionEnabled = request.ReliabilityDetectionEnabled ??
                    _storedSettings.ReliabilityDetectionEnabled,
                ReliabilityDetectionIntervalSeconds = request.ReliabilityDetectionIntervalSeconds ??
                    _storedSettings.ReliabilityDetectionIntervalSeconds,
                ReliabilityQuarantineHours = request.ReliabilityQuarantineHours ??
                    _storedSettings.ReliabilityQuarantineHours,
                DetectorPythonCommand = string.IsNullOrWhiteSpace(request.DetectorPythonCommand)
                    ? _storedSettings.DetectorPythonCommand
                    : request.DetectorPythonCommand.Trim(),
                DetectorWorkerPath = string.IsNullOrWhiteSpace(request.DetectorWorkerPath)
                    ? _storedSettings.DetectorWorkerPath
                    : request.DetectorWorkerPath.Trim(),
                DetectorPreset = string.IsNullOrWhiteSpace(request.DetectorPreset)
                    ? _storedSettings.DetectorPreset
                    : request.DetectorPreset.Trim(),
                DetectorBindings = detectorBindings
            };
            storedCredentials = storedCredentials with { DetectorApiKeys = detectorApiKeys };
            var persistedCredentials = storedSettings.PersistCredentials
                ? storedCredentials
                : new PersistentCredentials();
            var credentialsToSave = storedSettings.PersistCredentials &&
                (!_storedCredentialsUnavailable ||
                 HasCredentialValues(storedCredentials) ||
                 request.ClearPassword ||
                 request.ClearBearerToken)
                ? storedCredentials
                : null;
            var settings = ApplyEnvironmentSettings(storedSettings);
            var credentials = ApplyEnvironmentCredentials(persistedCredentials);

            _store.Save(storedSettings, credentialsToSave);
            lock (_stateLock)
            {
                _storedSettings = storedSettings;
                _storedCredentials = persistedCredentials;
                if (credentialsToSave is not null || !storedSettings.PersistCredentials)
                {
                    _storedCredentialsUnavailable = false;
                }
                _settings = settings;
                _credentials = credentials;
                _status = "配置已保存。";
                _statusKind = "success";
                _showProviderSeriesStatus = false;
                if (oldSettings.RoutingMode != settings.RoutingMode ||
                    oldSettings.GroupStickiness != settings.GroupStickiness ||
                    oldSettings.MinimumPriceMultiplier != settings.MinimumPriceMultiplier ||
                    oldSettings.MaximumPriceMultiplier != settings.MaximumPriceMultiplier ||
                    oldSettings.ConfidenceImpact != settings.ConfidenceImpact ||
                    oldSettings.MinimumConfidence != settings.MinimumConfidence ||
                    oldSettings.ProviderSeriesWeight != settings.ProviderSeriesWeight ||
                    oldSettings.ProviderSeriesCacheSeconds != settings.ProviderSeriesCacheSeconds ||
                    !string.Equals(oldSettings.ProviderSeriesRange, settings.ProviderSeriesRange, StringComparison.Ordinal) ||
                    !string.Equals(oldSettings.ProviderSeriesTimezone, settings.ProviderSeriesTimezone, StringComparison.Ordinal) ||
                    !string.Equals(oldSettings.BaseUrl, settings.BaseUrl, StringComparison.OrdinalIgnoreCase) ||
                    !oldSettings.BlacklistedGroupIds.SequenceEqual(settings.BlacklistedGroupIds) ||
                    oldSettings.ReliabilityDetectionEnabled != settings.ReliabilityDetectionEnabled ||
                    oldSettings.ReliabilityDetectionIntervalSeconds != settings.ReliabilityDetectionIntervalSeconds ||
                    oldSettings.ReliabilityQuarantineHours != settings.ReliabilityQuarantineHours ||
                    !string.Equals(oldSettings.DetectorPythonCommand, settings.DetectorPythonCommand, StringComparison.Ordinal) ||
                    !string.Equals(oldSettings.DetectorWorkerPath, settings.DetectorWorkerPath, StringComparison.Ordinal) ||
                    !string.Equals(oldSettings.DetectorPreset, settings.DetectorPreset, StringComparison.Ordinal) ||
                    !BindingsEqual(oldSettings.DetectorBindings, settings.DetectorBindings))
                {
                    _lastResult = null;
                }
                else if (!oldSettings.LunaSelectedKeyIds.SequenceEqual(settings.LunaSelectedKeyIds) &&
                         _lastResult is { } staleResult)
                {
                    _lastResult = staleResult with { LunaRoute = null };
                    _status = "配置已保存；Luna 选择已更新，请重新路由。";
                    _statusKind = "warning";
                }
            }

            ResetService();
            _nextAutoRun = DateTimeOffset.MinValue;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Saving Web router settings failed.");
            SetError(exception);
            throw new InvalidOperationException(SafeMessage(exception), exception);
        }
        finally
        {
            SetBusy(false);
            _operationGate.Release();
        }

        return GetDashboard();
    }

    public Task<WebDashboard> RunCycleAsync(
        bool dryRun,
        bool forceRefresh,
        CancellationToken cancellationToken) =>
        RunCycleCoreAsync(dryRun, forceRefresh, cancellationToken);

    public async Task<WebDashboard> RouteManuallyAsync(
        long groupId,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken);
        SetBusy(true);
        try
        {
            if (groupId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(groupId));
            }

            if (_settings.AutoRoutingEnabled)
            {
                var storedSettings = _storedSettings with { AutoRoutingEnabled = false };
                var settings = ApplyEnvironmentSettings(storedSettings);
                _store.Save(storedSettings, CredentialsForPersistence(storedSettings));
                lock (_stateLock)
                {
                    _storedSettings = storedSettings;
                    _settings = settings;
                }
            }

            EnsureService();
            var result = await _service!.RouteManuallyAsync(
                groupId,
                forceAccountRefresh: true,
                cancellationToken);
            lock (_stateLock)
            {
                var group = result.TargetGroup;
                _status = $"手动路由完成；切换 {result.ChangedKeyCount} 个，失败 {result.FailedKeyCount} 个。自动路由已关闭。";
                _statusKind = result.FailedKeyCount == 0 ? "success" : "error";
                _showProviderSeriesStatus = false;
                _lastUpdatedAt = result.CompletedAt;
                if (_lastResult is { } previous)
                {
                    _lastResult = previous with
                    {
                        Keys = result.Keys,
                        SelectedKeyIds = result.SelectedKeyIds,
                        KeyResults = result.KeyResults,
                        CompletedAt = result.CompletedAt
                    };
                }
                else
                {
                    _status = $"已将所选 Key 路由到 {group.Id} / {group.Name}；自动路由已关闭。";
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetError(exception);
        }
        finally
        {
            SetBusy(false);
            _operationGate.Release();
        }

        return GetDashboard();
    }

    public async Task<WebDashboard> SetAutoRoutingAsync(bool enabled, CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken);
        SetBusy(true);
        try
        {
            var storedSettings = _storedSettings with { AutoRoutingEnabled = enabled };
            var settings = ApplyEnvironmentSettings(storedSettings);
            _store.Save(storedSettings, CredentialsForPersistence(storedSettings));
            lock (_stateLock)
            {
                _storedSettings = storedSettings;
                _settings = settings;
                _status = enabled ? "自动路由已启动。" : "自动路由已停止。";
                _statusKind = "success";
                _showProviderSeriesStatus = false;
            }

            _nextAutoRun = enabled ? DateTimeOffset.MinValue : DateTimeOffset.MaxValue;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetError(exception);
        }
        finally
        {
            SetBusy(false);
            _operationGate.Release();
        }

        return GetDashboard();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool shouldRun;
            int interval;
            lock (_stateLock)
            {
                shouldRun = _settings.AutoRoutingEnabled && DateTimeOffset.UtcNow >= _nextAutoRun;
                interval = _settings.PollingIntervalSeconds;
            }

            if (shouldRun)
            {
                await RunCycleCoreAsync(dryRun: false, forceRefresh: false, stoppingToken);
                _nextAutoRun = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(interval, 30, 3600));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
        }
    }

    internal static bool ShouldHandleOperationCancellation(
        Exception exception,
        CancellationToken ownerToken) =>
        exception is OperationCanceledException && !ownerToken.IsCancellationRequested;

    private async Task<WebDashboard> RunCycleCoreAsync(
        bool dryRun,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken);
        SetBusy(true);
        try
        {
            PrepareForKeyDiscovery();
            EnsureService();
            var result = await _service!.RunOnceAsync(dryRun, forceRefresh, cancellationToken);
            lock (_stateLock)
            {
                _lastResult = result;
                _lastUpdatedAt = result.CompletedAt;
                var lunaStatus = BuildLunaStatus(result.LunaRoute);
                var reliabilityStatus = BuildReliabilityStatus(result.Reliability);
                _status = $"{ReasonText(result.Decision.Reason)}；切换 {result.ChangedKeyCount} 个，失败 {result.FailedKeyCount} 个。" +
                    (lunaStatus is null ? string.Empty : $" {lunaStatus}") +
                    (reliabilityStatus is null ? string.Empty : $" {reliabilityStatus}");
                _statusKind = result.FailedKeyCount > 0
                    ? "error"
                    : result.LunaRoute is { HealthAvailable: false } ||
                        result.Reliability?.Keys.Any(key =>
                            key.Status is ChannelReliabilityStatus.Unavailable or
                                ChannelReliabilityStatus.EvidenceInsufficient) == true ||
                        result.ProviderSeriesStatus.IsDegraded ||
                        result.ProviderCacheHitRateStatus.IsDegraded
                        ? "warning"
                        : "success";
                _showProviderSeriesStatus = true;
            }
        }
        catch (OperationCanceledException exception)
            when (ShouldHandleOperationCancellation(exception, cancellationToken))
        {
            _logger.LogWarning(
                exception,
                "路由周期请求被上游超时取消，本轮已结束，后台服务将继续运行。");
            SetError(exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetError(exception);
        }
        finally
        {
            SetBusy(false);
            _operationGate.Release();
        }

        return GetDashboard();
    }

    private void PrepareForKeyDiscovery()
    {
        bool resetSelection;
        PersistentAppSettings? storedSettings = null;
        lock (_stateLock)
        {
            resetSelection = _lastResult is null &&
                _settings.KeySelectionInitialized &&
                _settings.SelectedKeyIds.Length == 0 &&
                _settings.LunaSelectedKeyIds.Length == 0;
            if (resetSelection)
            {
                storedSettings = _storedSettings with { KeySelectionInitialized = false };
                _storedSettings = storedSettings;
                _settings = ApplyEnvironmentSettings(storedSettings);
            }
        }

        if (resetSelection && storedSettings is not null)
        {
            _store.Save(
                storedSettings,
                CredentialsForPersistence(storedSettings));
            ResetService();
        }
    }

    private WebDashboard BuildDashboard()
    {
        var settings = _settings;
        var result = _lastResult;
        var effectiveSelectedIds = settings.KeySelectionInitialized
            ? settings.SelectedKeyIds
            : result?.SelectedKeyIds.ToArray() ?? settings.SelectedKeyIds;
        var effectiveLunaSelectedIds = NormalizeIds(settings.LunaSelectedKeyIds);
        var selectedIds = effectiveSelectedIds.ToHashSet();
        var lunaSelectedIds = effectiveLunaSelectedIds.ToHashSet();
        var blacklistedIds = settings.BlacklistedGroupIds.ToHashSet();
        var policy = settings.CreatePolicy();
        var groupsById = result?.Groups.GroupBy(group => group.Id)
            .ToDictionary(group => group.Key, group => group.First()) ?? [];
        var reliabilityGroups = result?.Reliability?.Groups
            .GroupBy(group => group.GroupId)
            .ToDictionary(group => group.Key, group => group.First()) ?? [];
        var reliabilityKeys = result?.Reliability?.Keys
            .GroupBy(key => key.KeyId)
            .ToDictionary(key => key.Key, key => key.First()) ?? [];
        var targetId = result?.Decision.Target?.Group.Id;

        var groups = (result?.Groups ?? [])
            .Where(group => group.Platform.Equals(settings.Platform, StringComparison.OrdinalIgnoreCase))
            .GroupBy(group => group.Id)
            .Select(group => group.First())
            .OrderBy(group => group.Id)
            .Select(group => new WebGroupRow(
                group.Id,
                group.Name,
                group.Platform,
                group.Status,
                blacklistedIds.Contains(group.Id)))
            .ToArray();

        var providers = (result?.Providers ?? [])
            .Where(provider => provider.Platform.Equals(settings.Platform, StringComparison.OrdinalIgnoreCase))
            .Select(provider => BuildProviderRow(
                provider,
                groupsById,
                result!.UserGroupRates,
                result!.Evaluation,
                policy,
                targetId,
                blacklistedIds,
                reliabilityGroups))
            .OrderByDescending(provider => provider.WeightedScore)
            .ThenBy(provider => provider.GroupId)
            .ToArray();

        var keys = (result?.Keys ?? [])
            .Select(key =>
            {
                reliabilityKeys.TryGetValue(key.Id, out var reliability);
                return new WebKeyRow(
                    key.Id,
                    key.Name,
                    key.Status,
                    key.GroupId,
                    key.Group?.Name ?? "未绑定",
                    selectedIds.Contains(key.Id),
                    lunaSelectedIds.Contains(key.Id))
                {
                    ReliabilityState = reliability?.Status.ToString() ?? ChannelReliabilityStatus.Unconfigured.ToString(),
                    ReliabilityQuarantinedUntil = reliability?.QuarantinedUntil,
                    ReliabilityModels = reliability?.Models ?? []
                };
            })
            .ToArray();

        var target = result?.Decision.Target;
        var candidateSummary = target is null
            ? result is null ? "目标分组：-" : "目标分组：无可用候选"
            : $"目标分组：{target.Group.Id} / 方案：{DisplayPlan(target.Provider, target.Group)} / " +
                $"{target.EffectiveMultiplier:0.####}x / {FormatLatency(target.Provider.FirstTokenLatencyMs)}";
        var lunaSummary = BuildLunaSummary(result?.LunaRoute, effectiveLunaSelectedIds.Length);
        if (lunaSummary is not null)
        {
            candidateSummary += $" · {lunaSummary}";
        }

        return new WebDashboard(
            new WebSettings(
                settings.BaseUrl,
                _credentials.Email,
                !string.IsNullOrWhiteSpace(_credentials.Password),
                !string.IsNullOrWhiteSpace(_credentials.BearerToken),
                settings.RoutingMode,
                settings.CreatePolicy().MinimumScoreAdvantageToSwitch,
                settings.MinimumPriceMultiplier,
                settings.MaximumPriceMultiplier,
                settings.ConfidenceImpact,
                settings.MinimumConfidence,
                settings.ProviderSeriesWeight,
                settings.ProviderSeriesCacheSeconds,
                settings.ProviderSeriesRange,
                settings.ProviderSeriesTimezone,
                settings.PollingIntervalSeconds,
                settings.PersistCredentials,
                _store.CanPersistCredentials,
                _storedCredentialsUnavailable,
                _store.CredentialProtection,
                settings.ThemeMode,
                effectiveSelectedIds,
                effectiveLunaSelectedIds,
                settings.BlacklistedGroupIds)
            {
                ReliabilityDetectionEnabled = settings.ReliabilityDetectionEnabled,
                ReliabilityDetectionIntervalSeconds = settings.ReliabilityDetectionIntervalSeconds,
                ReliabilityQuarantineHours = settings.ReliabilityQuarantineHours,
                DetectorPythonCommand = settings.DetectorPythonCommand,
                DetectorWorkerPath = settings.DetectorWorkerPath,
                DetectorPreset = settings.DetectorPreset,
                DetectorBindings = settings.DetectorBindings
            },
            providers,
            groups,
            keys,
            _isBusy,
            settings.AutoRoutingEnabled,
            _status,
            _statusKind,
            result is null || !_showProviderSeriesStatus
                ? null
                : new WebProviderSeriesStatus(
                    result.ProviderSeriesStatus.Available,
                    result.ProviderSeriesStatus.FromCache,
                    result.ProviderSeriesStatus.IsDegraded,
                    result.ProviderSeriesStatus.Message),
            result is null || !_showProviderSeriesStatus
                ? null
                : new WebProviderCacheHitRateStatus(
                    result.ProviderCacheHitRateStatus.Available,
                    result.ProviderCacheHitRateStatus.FromCache,
                    result.ProviderCacheHitRateStatus.IsDegraded,
                    result.ProviderCacheHitRateStatus.Message),
            candidateSummary,
            $"API-only / {settings.RoutingMode}",
            _lastUpdatedAt)
        {
            LunaRoute = BuildLunaRoute(result?.LunaRoute, effectiveLunaSelectedIds.Length),
            Reliability = result?.Reliability
        };
    }

    private static WebProviderRow BuildProviderRow(
        ProviderStatus provider,
        IReadOnlyDictionary<long, GroupInfo> groups,
        IReadOnlyDictionary<long, double> userGroupRates,
        RouteEvaluation evaluation,
        BalancedRoutingPolicy policy,
        long? targetGroupId,
        IReadOnlySet<long> blacklistedGroupIds,
        IReadOnlyDictionary<long, ChannelReliabilityGroupSummary> reliabilityGroups)
    {
        var candidate = evaluation.EligibleCandidates.FirstOrDefault(item =>
            item.Group.Id == provider.GroupId && item.Provider.Id == provider.Id);
        var multiplier = provider.GroupId is { } groupId &&
            userGroupRates.TryGetValue(groupId, out var userRate)
                ? userRate
                : provider.PriceMultiplier;
        var priceOutOfRange = !double.IsFinite(multiplier) ||
            !RoutingEngine.IsWithinPriceRange(multiplier, policy);
        var score = candidate is null ? null : RoutingEngine.CalculateWeightedScore(evaluation, candidate);
        var reliability = provider.GroupId is { } reliabilityGroupId &&
            reliabilityGroups.TryGetValue(reliabilityGroupId, out var reliabilitySummary)
            ? reliabilitySummary
            : null;
        var reliabilityQuarantined = reliability?.Status == ChannelReliabilityStatus.Quarantined;
        var state = provider.GroupId is { } groupId && blacklistedGroupIds.Contains(groupId)
            ? "黑名单"
            : reliabilityQuarantined
                ? "掺水隔离"
                : priceOutOfRange
                    ? "价格范围外"
                    : provider.GroupId == targetGroupId
                        ? "推荐"
                        : !provider.Enabled ? "停用" : !provider.Available ? "异常" :
                            provider.HasWarnings ? "警告" : "可用";
        var canManualRoute = provider.GroupId is { } manualGroupId &&
            groups.TryGetValue(manualGroupId, out var group) &&
            group.Status.Equals("active", StringComparison.OrdinalIgnoreCase) &&
            !blacklistedGroupIds.Contains(manualGroupId) &&
            !reliabilityQuarantined &&
            !priceOutOfRange;

        return new WebProviderRow(
            provider.Id,
            provider.GroupId,
            provider.GroupId is { } id && groups.TryGetValue(id, out var info)
                ? DisplayPlan(provider, info)
                : provider.PlanType,
            double.IsFinite(multiplier) ? multiplier : null,
            provider.FirstTokenLatencyMs is >= 0 and var latency && double.IsFinite(latency) ? latency : null,
            provider.LatencyConfidence,
            provider.CacheHitRate,
            provider.UsageSampleCount,
            score,
            state,
            provider.CheckedAt,
            canManualRoute,
            provider.GroupId == targetGroupId)
        {
            ReliabilityState = reliability?.Status.ToString() ?? ChannelReliabilityStatus.Unconfigured.ToString(),
            ReliabilityQuarantinedUntil = reliability?.QuarantinedUntil,
            ReliabilityModels = reliability?.Models ?? []
        };
    }

    private void EnsureService()
    {
        if (_service is not null)
        {
            return;
        }

        _profileLock = ProfileLock.TryAcquire(_store.StorageDirectory)
            ?? throw new InvalidOperationException("另一个 AIHubRouter 实例正在使用当前 profile。");
        var serviceSettings = _settings;
        _service = new RoutingService(
            serviceSettings,
            _credentials,
            new JsonRouteStateStore(_store.StorageDirectory),
            persistCredentials: (updated, token) =>
            {
                token.ThrowIfCancellationRequested();
                PersistentAppSettings storedSettings;
                PersistentCredentials storedCredentials;
                bool credentialsUnavailable;
                lock (_stateLock)
                {
                    storedCredentials = MergeStoredCredentials(_storedCredentials, updated);
                    storedSettings = _storedSettings;
                    credentialsUnavailable = _storedCredentialsUnavailable;
                }

                var credentialsSaved = false;
                if (storedSettings.PersistCredentials && !credentialsUnavailable)
                {
                    _store.Save(storedSettings, storedCredentials);
                    credentialsSaved = true;
                }

                lock (_stateLock)
                {
                    _credentials = updated;
                    if (credentialsSaved)
                    {
                        _storedCredentials = storedCredentials;
                        _storedCredentialsUnavailable = false;
                    }
                }

                return Task.CompletedTask;
            },
            channelQuarantineStore: new JsonChannelQuarantineStore(_store.StorageDirectory));
    }

    private void ResetService()
    {
        _service?.Dispose();
        _service = null;
        _profileLock?.Dispose();
        _profileLock = null;
    }

    private void SetBusy(bool busy)
    {
        lock (_stateLock)
        {
            _isBusy = busy;
        }
    }

    private void SetError(Exception exception)
    {
        lock (_stateLock)
        {
            _status = SafeMessage(exception);
            _statusKind = "error";
            _showProviderSeriesStatus = false;
        }
    }

    private static void ValidateSettings(SettingsUpdateRequest request)
    {
        if (!Uri.TryCreate(request.BaseUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("站点地址无效，仅支持 HTTP 或 HTTPS。");
        }

        if (request.PollingIntervalSeconds is < 30 or > 3600)
        {
            throw new ArgumentOutOfRangeException(nameof(request.PollingIntervalSeconds), "轮询间隔必须在 30 到 3600 秒之间。");
        }

        if (request.ProviderSeriesWeight is { } providerSeriesWeight &&
            (providerSeriesWeight is < 0 or > 1 || !double.IsFinite(providerSeriesWeight)))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.ProviderSeriesWeight),
                "供应商序列权重必须是 0 到 1 之间的有限数值。");
        }

        if (request.ProviderSeriesCacheSeconds is < 30 or > 3600)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.ProviderSeriesCacheSeconds),
                "供应商序列缓存必须在 30 到 3600 秒之间。");
        }

        if (request.ProviderSeriesRange is not null &&
            string.IsNullOrWhiteSpace(request.ProviderSeriesRange))
        {
            throw new ArgumentException("供应商序列范围不能为空。", nameof(request.ProviderSeriesRange));
        }

        if (request.ProviderSeriesTimezone is not null &&
            string.IsNullOrWhiteSpace(request.ProviderSeriesTimezone))
        {
            throw new ArgumentException("供应商序列时区不能为空。", nameof(request.ProviderSeriesTimezone));
        }

        if (request.ReliabilityDetectionIntervalSeconds is < 60 or > 86_400)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.ReliabilityDetectionIntervalSeconds),
                "可靠性检测间隔必须在 60 到 86400 秒之间。" );
        }

        if (request.ReliabilityQuarantineHours is < 1 or > 168)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.ReliabilityQuarantineHours),
                "可靠性隔离时长必须在 1 到 168 小时之间。" );
        }

        if (request.DetectorPythonCommand is not null &&
            string.IsNullOrWhiteSpace(request.DetectorPythonCommand))
        {
            throw new ArgumentException("检测 Python 命令不能为空。", nameof(request.DetectorPythonCommand));
        }

        if (request.DetectorWorkerPath is not null &&
            string.IsNullOrWhiteSpace(request.DetectorWorkerPath))
        {
            throw new ArgumentException("检测 worker 路径不能为空。", nameof(request.DetectorWorkerPath));
        }

        if (request.DetectorPreset is not null &&
            !string.Equals(request.DetectorPreset.Trim(), "low", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("当前只支持官方 low 检测 preset。", nameof(request.DetectorPreset));
        }

        if (request.DetectorBindings is not null)
        {
            var bindingIds = new HashSet<long>();
            foreach (var binding in request.DetectorBindings)
            {
                if (binding is null || binding.KeyId <= 0 || !bindingIds.Add(binding.KeyId))
                {
                    throw new ArgumentException("检测绑定的 Key ID 必须为正数且不能重复。", nameof(request.DetectorBindings));
                }

                if (!Uri.TryCreate(binding.BaseUrl?.Trim(), UriKind.Absolute, out var bindingUri) ||
                    (bindingUri.Scheme != Uri.UriSchemeHttp && bindingUri.Scheme != Uri.UriSchemeHttps))
                {
                    throw new ArgumentException("检测绑定地址仅支持 HTTP 或 HTTPS。", nameof(request.DetectorBindings));
                }

                if ((binding.Models ?? []).Any(model => !DetectorModelNames.IsSupported(model)))
                {
                    throw new ArgumentException("检测绑定包含不支持的模型。", nameof(request.DetectorBindings));
                }
            }
        }

        if (request.GroupStickiness < 0 || !double.IsFinite(request.GroupStickiness))
        {
            throw new ArgumentOutOfRangeException(nameof(request.GroupStickiness), "分组粘性必须是非负有限数值。");
        }

        if (request.MinimumPriceMultiplier < 0 ||
            !double.IsFinite(request.MinimumPriceMultiplier) ||
            !double.IsFinite(request.MaximumPriceMultiplier) ||
            request.MaximumPriceMultiplier < request.MinimumPriceMultiplier)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.MaximumPriceMultiplier),
                "价格范围必须是非负有限数值，且最小值不能大于最大值。");
        }

        var policy = new BalancedRoutingPolicy
        {
            ConfidenceImpact = request.ConfidenceImpact,
            MinimumConfidence = request.MinimumConfidence
        };
        policy.Validate();
    }

    private static long[] NormalizeIds(IEnumerable<long>? ids) =>
        (ids ?? []).Where(id => id > 0).Distinct().Order().ToArray();

    private static DetectorBinding[] NormalizeDetectorBindings(IEnumerable<DetectorBinding> bindings) =>
        (bindings ?? [])
            .Where(binding => binding is not null)
            .Where(binding => binding.KeyId > 0)
            .GroupBy(binding => binding.KeyId)
            .Select(group => group.Last())
            .Select(binding => binding with
            {
                BaseUrl = binding.BaseUrl.Trim().TrimEnd('/'),
                Models = (binding.Models ?? [])
                    .Select(DetectorModelNames.Normalize)
                    .Where(model => model is not null)
                    .Select(model => model!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order()
                    .ToArray()
            })
            .OrderBy(binding => binding.KeyId)
            .ToArray();

    private static bool BindingsEqual(
        IReadOnlyList<DetectorBinding> left,
        IReadOnlyList<DetectorBinding> right)
    {
        var leftBindings = left.OrderBy(binding => binding.KeyId).ToArray();
        var rightBindings = right.OrderBy(binding => binding.KeyId).ToArray();
        if (leftBindings.Length != rightBindings.Length)
        {
            return false;
        }

        for (var index = 0; index < leftBindings.Length; index++)
        {
            var first = leftBindings[index];
            var second = rightBindings[index];
            if (first.KeyId != second.KeyId ||
                first.Enabled != second.Enabled ||
                !string.Equals(first.BaseUrl.Trim().TrimEnd('/'), second.BaseUrl.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
                !(first.Models ?? [])
                    .Select(DetectorModelNames.Normalize)
                    .Where(model => model is not null)
                    .Select(model => model!)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(
                        (second.Models ?? [])
                            .Select(DetectorModelNames.Normalize)
                            .Where(model => model is not null)
                            .Select(model => model!)
                            .Order(StringComparer.OrdinalIgnoreCase),
                        StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static string? BuildLunaSummary(LunaRouteResult? lunaRoute, int configuredKeyCount)
    {
        if (lunaRoute is null)
        {
            return configuredKeyCount > 0 ? $"Luna 目标：- / 已选 {configuredKeyCount} 个 Key" : null;
        }

        var target = lunaRoute.Decision?.Target;
        var targetText = target is null
            ? "无可用候选"
            : $"{target.Group.Id} / 方案：{DisplayPlan(target.Provider, target.Group)}";
        return $"Luna 目标：{targetText} / 过滤 {lunaRoute.FilteredGroupCount} 个分组";
    }

    private static WebLunaRoute BuildLunaRoute(
        LunaRouteResult? lunaRoute,
        int configuredKeyCount)
    {
        var selectedKeyCount = lunaRoute?.SelectedKeyIds.Count ?? configuredKeyCount;
        var configured = configuredKeyCount > 0 || selectedKeyCount > 0;
        if (lunaRoute is null)
        {
            return new WebLunaRoute(
                configured,
                false,
                false,
                false,
                configured
                    ? $"已配置 {configuredKeyCount} 个 Luna Key，尚未运行。"
                    : "未配置 Luna Key。",
                0,
                selectedKeyCount,
                null,
                null,
                null,
                null,
                "未运行");
        }

        var target = lunaRoute.Decision?.Target;
        return new WebLunaRoute(
            configured,
            true,
            lunaRoute.HealthAvailable,
            target is not null,
            lunaRoute.HealthMessage,
            lunaRoute.FilteredGroupCount,
            selectedKeyCount,
            target?.Group.Id,
            target is null ? null : DisplayPlan(target.Provider, target.Group),
            target?.EffectiveMultiplier,
            target?.Provider.FirstTokenLatencyMs,
            lunaRoute.Decision is { } decision
                ? ReasonText(decision.Reason)
                : "无可用候选");
    }

    private static string? BuildLunaStatus(LunaRouteResult? lunaRoute) =>
        lunaRoute is null ? null : $"Luna：{lunaRoute.HealthMessage}";

    private static string? BuildReliabilityStatus(ChannelReliabilityCycleResult? reliability)
    {
        if (reliability is null)
        {
            return null;
        }

        return reliability.ExcludedGroupIds.Count > 0
            ? $"可靠性隔离 {reliability.ExcludedGroupIds.Count} 个分组"
            : reliability.Enabled
                ? "可靠性检测已完成"
                : null;
    }

    private static PersistentAppSettings ApplyEnvironmentSettings(PersistentAppSettings settings)
    {
        var baseUrl = Environment.GetEnvironmentVariable("AIHUB_BASE_URL");
        return string.IsNullOrWhiteSpace(baseUrl) ? settings : settings with { BaseUrl = baseUrl.Trim() };
    }

    private static PersistentCredentials ApplyEnvironmentCredentials(PersistentCredentials credentials) =>
        credentials with
        {
            Email = Environment.GetEnvironmentVariable("AIHUB_EMAIL") ?? credentials.Email,
            Password = Environment.GetEnvironmentVariable("AIHUB_PASSWORD") ?? credentials.Password,
            BearerToken = Environment.GetEnvironmentVariable("AIHUB_TOKEN") ?? credentials.BearerToken,
            RefreshToken = Environment.GetEnvironmentVariable("AIHUB_REFRESH_TOKEN") ?? credentials.RefreshToken,
            Cookie = Environment.GetEnvironmentVariable("AIHUB_COOKIE") ?? credentials.Cookie,
            UserAgent = Environment.GetEnvironmentVariable("AIHUB_USER_AGENT") ?? credentials.UserAgent
        };

    private static PersistentCredentials MergeStoredCredentials(
        PersistentCredentials stored,
        PersistentCredentials effective)
    {
        var runtimeTokenChainOverride = HasEnvironmentVariable("AIHUB_PASSWORD") ||
            HasEnvironmentVariable("AIHUB_TOKEN") ||
            HasEnvironmentVariable("AIHUB_REFRESH_TOKEN") ||
            HasEnvironmentVariable("AIHUB_COOKIE");
        return stored with
        {
            Email = HasEnvironmentVariable("AIHUB_EMAIL") ? stored.Email : effective.Email,
            Password = HasEnvironmentVariable("AIHUB_PASSWORD") ? stored.Password : effective.Password,
            BearerToken = runtimeTokenChainOverride ? stored.BearerToken : effective.BearerToken,
            RefreshToken = runtimeTokenChainOverride ? stored.RefreshToken : effective.RefreshToken,
            AccessTokenExpiresAt = runtimeTokenChainOverride
                ? stored.AccessTokenExpiresAt
                : effective.AccessTokenExpiresAt,
            Cookie = HasEnvironmentVariable("AIHUB_COOKIE") ? stored.Cookie : effective.Cookie,
            UserAgent = HasEnvironmentVariable("AIHUB_USER_AGENT") ? stored.UserAgent : effective.UserAgent
        };
    }

    private static bool HasEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name) is not null;

    private PersistentCredentials? CredentialsForPersistence(PersistentAppSettings settings) =>
        settings.PersistCredentials && !_storedCredentialsUnavailable
            ? _storedCredentials
            : null;

    private static bool HasCredentialValues(PersistentCredentials credentials) =>
        !string.IsNullOrWhiteSpace(credentials.Email) ||
        !string.IsNullOrWhiteSpace(credentials.Password) ||
        !string.IsNullOrWhiteSpace(credentials.BearerToken) ||
        !string.IsNullOrWhiteSpace(credentials.RefreshToken) ||
        credentials.AccessTokenExpiresAt is not null ||
        !string.IsNullOrWhiteSpace(credentials.Cookie) ||
        !string.IsNullOrWhiteSpace(credentials.UserAgent) ||
        (credentials.DetectorApiKeys ?? []).Any(pair =>
            pair.Key > 0 && !string.IsNullOrWhiteSpace(pair.Value));

    private static string DisplayPlan(ProviderStatus provider, GroupInfo group) =>
        string.IsNullOrWhiteSpace(provider.PlanType) ? group.Name : provider.PlanType;

    private static string FormatLatency(double? latency) =>
        latency is >= 0 and var value && double.IsFinite(value) ? $"{value:0} ms" : "未知";

    private static string SafeMessage(Exception exception) => exception switch
    {
        AIHubApiException api => api.Message,
        HttpRequestException => "网络连接失败。",
        TaskCanceledException => "请求超时。",
        UnauthorizedAccessException => "无法写入配置数据。请检查 Docker 数据卷权限。",
        IOException => "配置保存失败。请检查 Docker 数据卷权限和剩余空间。",
        System.Security.Cryptography.CryptographicException => "认证加密失败。请确认 AIHUB_ROUTER_MASTER_KEY 未变更。",
        InvalidOperationException invalid => invalid.Message,
        ArgumentException argument => argument.Message,
        _ => "操作失败。"
    };

    private static string ReasonText(RouteDecisionReason reason) => reason switch
    {
        RouteDecisionReason.NoCandidate => "没有可用候选",
        RouteDecisionReason.InitialRoute => "已选择初始路由",
        RouteDecisionReason.CurrentRouteInvalid => "当前路由不可用",
        RouteDecisionReason.AlreadyOptimal => "当前路由已是最优",
        RouteDecisionReason.ScoreAdvantageTooSmall => "新候选优势不足，保持当前路由",
        RouteDecisionReason.BetterPrice => "发现更低价格",
        RouteDecisionReason.FasterForWeightedTradeoff => "发现更优速度与价格组合",
        _ => reason.ToString()
    };

    public override void Dispose()
    {
        ResetService();
        _operationGate.Dispose();
        base.Dispose();
    }
}
