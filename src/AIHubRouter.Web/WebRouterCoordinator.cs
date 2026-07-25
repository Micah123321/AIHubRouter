using AIHubRouter.Core;

namespace AIHubRouter.Web;

public sealed class WebRouterCoordinator : BackgroundService
{
    private readonly AppSettingsStore _store = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _stateLock = new();
    private PersistentAppSettings _settings;
    private PersistentCredentials _credentials;
    private RoutingService? _service;
    private ProfileLock? _profileLock;
    private RoutingCycleResult? _lastResult;
    private bool _isBusy;
    private string _status = "就绪";
    private string _statusKind = "neutral";
    private DateTimeOffset? _lastUpdatedAt;
    private DateTimeOffset _nextAutoRun = DateTimeOffset.MinValue;

    public WebRouterCoordinator()
    {
        var snapshot = _store.Load();
        _settings = ApplyEnvironmentSettings(snapshot.Settings);
        _credentials = ApplyEnvironmentCredentials(snapshot.Credentials ?? new PersistentCredentials());
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
            var oldSettings = _settings;
            var credentials = _credentials with
            {
                Email = request.Email.Trim(),
                Password = request.ClearPassword
                    ? string.Empty
                    : request.Password is null ? _credentials.Password : request.Password,
                BearerToken = request.ClearBearerToken
                    ? string.Empty
                    : request.BearerToken is null ? _credentials.BearerToken :
                        CredentialParser.NormalizeBearerToken(request.BearerToken)
            };
            var settings = _settings with
            {
                BaseUrl = request.BaseUrl.Trim().TrimEnd('/'),
                RoutingMode = request.RoutingMode,
                GroupStickiness = request.GroupStickiness,
                PollingIntervalSeconds = Math.Clamp(request.PollingIntervalSeconds, 30, 3600),
                PersistCredentials = request.PersistCredentials,
                ThemeMode = request.ThemeMode,
                KeySelectionInitialized = _lastResult?.Keys.Count > 0
                    ? true
                    : request.SelectedKeyIds.Length > 0 ||
                        (_settings.KeySelectionInitialized && _settings.SelectedKeyIds.Length > 0),
                SelectedKeyIds = request.SelectedKeyIds.Where(id => id > 0).Distinct().Order().ToArray(),
                BlacklistedGroupIds = request.BlacklistedGroupIds.Where(id => id > 0).Distinct().Order().ToArray()
            };

            if (settings.PersistCredentials && !_store.CanPersistCredentials)
            {
                throw new InvalidOperationException(_store.CredentialProtection);
            }

            _store.Save(settings, settings.PersistCredentials ? credentials : null);
            lock (_stateLock)
            {
                _settings = settings;
                _credentials = credentials;
                _status = "配置已保存。";
                _statusKind = "success";
                if (oldSettings.RoutingMode != settings.RoutingMode ||
                    oldSettings.GroupStickiness != settings.GroupStickiness ||
                    !string.Equals(oldSettings.BaseUrl, settings.BaseUrl, StringComparison.OrdinalIgnoreCase) ||
                    !oldSettings.BlacklistedGroupIds.SequenceEqual(settings.BlacklistedGroupIds))
                {
                    _lastResult = null;
                }
            }

            ResetService();
            _nextAutoRun = DateTimeOffset.MinValue;
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
                var settings = _settings with { AutoRoutingEnabled = false };
                _store.Save(settings, settings.PersistCredentials ? _credentials : null);
                lock (_stateLock)
                {
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
            var settings = _settings with { AutoRoutingEnabled = enabled };
            _store.Save(settings, settings.PersistCredentials ? _credentials : null);
            lock (_stateLock)
            {
                _settings = settings;
                _status = enabled ? "自动路由已启动。" : "自动路由已停止。";
                _statusKind = "success";
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
                _status = $"{ReasonText(result.Decision.Reason)}；切换 {result.ChangedKeyCount} 个，失败 {result.FailedKeyCount} 个。";
                _statusKind = result.FailedKeyCount == 0 ? "success" : "error";
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

    private void PrepareForKeyDiscovery()
    {
        bool resetSelection;
        lock (_stateLock)
        {
            resetSelection = _lastResult is null &&
                _settings.KeySelectionInitialized &&
                _settings.SelectedKeyIds.Length == 0;
            if (resetSelection)
            {
                _settings = _settings with { KeySelectionInitialized = false };
            }
        }

        if (resetSelection)
        {
            _store.Save(_settings, _settings.PersistCredentials ? _credentials : null);
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
        var selectedIds = effectiveSelectedIds.ToHashSet();
        var blacklistedIds = settings.BlacklistedGroupIds.ToHashSet();
        var groupsById = result?.Groups.GroupBy(group => group.Id)
            .ToDictionary(group => group.Key, group => group.First()) ?? [];
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
                result!.Evaluation,
                targetId,
                blacklistedIds))
            .OrderByDescending(provider => provider.WeightedScore)
            .ThenBy(provider => provider.GroupId)
            .ToArray();

        var keys = (result?.Keys ?? [])
            .Select(key => new WebKeyRow(
                key.Id,
                key.Name,
                key.Status,
                key.GroupId,
                key.Group?.Name ?? "未绑定",
                selectedIds.Contains(key.Id)))
            .ToArray();

        var target = result?.Decision.Target;
        var candidateSummary = target is null
            ? result is null ? "目标分组：-" : "目标分组：无可用候选"
            : $"目标分组：{target.Group.Id} / 方案：{DisplayPlan(target.Provider, target.Group)} / " +
                $"{target.EffectiveMultiplier:0.####}x / {FormatLatency(target.Provider.FirstTokenLatencyMs)}";

        return new WebDashboard(
            new WebSettings(
                settings.BaseUrl,
                _credentials.Email,
                !string.IsNullOrWhiteSpace(_credentials.Password),
                !string.IsNullOrWhiteSpace(_credentials.BearerToken),
                settings.RoutingMode,
                settings.CreatePolicy().MinimumScoreAdvantageToSwitch,
                settings.PollingIntervalSeconds,
                settings.PersistCredentials,
                _store.CanPersistCredentials,
                _store.CredentialProtection,
                settings.ThemeMode,
                effectiveSelectedIds,
                settings.BlacklistedGroupIds),
            providers,
            groups,
            keys,
            _isBusy,
            settings.AutoRoutingEnabled,
            _status,
            _statusKind,
            candidateSummary,
            $"API-only / {settings.RoutingMode}",
            _lastUpdatedAt);
    }

    private static WebProviderRow BuildProviderRow(
        ProviderStatus provider,
        IReadOnlyDictionary<long, GroupInfo> groups,
        RouteEvaluation evaluation,
        long? targetGroupId,
        IReadOnlySet<long> blacklistedGroupIds)
    {
        var candidate = evaluation.EligibleCandidates.FirstOrDefault(item =>
            item.Group.Id == provider.GroupId && item.Provider.Id == provider.Id);
        var multiplier = candidate?.EffectiveMultiplier ?? provider.PriceMultiplier;
        var score = candidate is null ? null : RoutingEngine.CalculateWeightedScore(evaluation, candidate);
        var state = provider.GroupId is { } groupId && blacklistedGroupIds.Contains(groupId)
            ? "黑名单"
            : provider.GroupId == targetGroupId
                ? "推荐"
                : !provider.Enabled ? "停用" : !provider.Available ? "异常" :
                    provider.HasWarnings ? "警告" : "可用";
        var canManualRoute = provider.GroupId is { } manualGroupId &&
            groups.TryGetValue(manualGroupId, out var group) &&
            group.Status.Equals("active", StringComparison.OrdinalIgnoreCase) &&
            !blacklistedGroupIds.Contains(manualGroupId);

        return new WebProviderRow(
            provider.Id,
            provider.GroupId,
            provider.GroupId is { } id && groups.TryGetValue(id, out var info)
                ? DisplayPlan(provider, info)
                : provider.PlanType,
            double.IsFinite(multiplier) ? multiplier : null,
            provider.FirstTokenLatencyMs is >= 0 and var latency && double.IsFinite(latency) ? latency : null,
            provider.LatencyConfidence,
            provider.UsageSampleCount,
            score,
            state,
            provider.CheckedAt,
            canManualRoute,
            provider.GroupId == targetGroupId);
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
                PersistentAppSettings currentSettings;
                lock (_stateLock)
                {
                    _credentials = updated;
                    currentSettings = _settings;
                }

                if (currentSettings.PersistCredentials)
                {
                    _store.Save(currentSettings, updated);
                }

                return Task.CompletedTask;
            });
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

        if (request.GroupStickiness < 0 || !double.IsFinite(request.GroupStickiness))
        {
            throw new ArgumentOutOfRangeException(nameof(request.GroupStickiness), "分组粘性必须是非负有限数值。");
        }
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

    private static string DisplayPlan(ProviderStatus provider, GroupInfo group) =>
        string.IsNullOrWhiteSpace(provider.PlanType) ? group.Name : provider.PlanType;

    private static string FormatLatency(double? latency) =>
        latency is >= 0 and var value && double.IsFinite(value) ? $"{value:0} ms" : "未知";

    private static string SafeMessage(Exception exception) => exception switch
    {
        AIHubApiException api => api.Message,
        HttpRequestException => "网络连接失败。",
        TaskCanceledException => "请求超时。",
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
