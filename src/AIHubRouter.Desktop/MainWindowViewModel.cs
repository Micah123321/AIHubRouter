using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Styling;
using AIHubRouter.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIHubRouter.Desktop;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    public static IReadOnlyList<ThemeChoice> ThemeChoices { get; } =
    [
        new(AppThemeMode.System, "跟随系统"),
        new(AppThemeMode.Light, "浅色"),
        new(AppThemeMode.Dark, "深色")
    ];

    private readonly AppSettingsStore _store = new();
    private RoutingService? _service;
    private ProfileLock? _profileLock;
    private CancellationTokenSource? _autoRoutingCancellation;
    private PersistentCredentials _loadedCredentials = new();
    private string? _providerSortField = "WeightedScore";
    private bool _providerSortDescending = true;
    private bool _routingSettingsStale;
    private int _routingSettingsVersion;

    [ObservableProperty] private string _baseUrl = "https://aihub.top";
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _bearerToken = string.Empty;
    [ObservableProperty] private decimal _pollingIntervalSeconds = 60;
    [ObservableProperty] private bool _persistCredentials;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ManualRouteCommand))]
    [NotifyPropertyChangedFor(nameof(CanChangeRoutingMode))]
    private bool _isBusy;
    [ObservableProperty] private bool _autoRouting;
    [ObservableProperty] private string _status = "就绪";
    [ObservableProperty] private bool _statusIsSuccess;
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private string _candidateSummary = "目标分组：-";
    [ObservableProperty] private string _connectionSummary = "API-only / Balanced";
    [ObservableProperty] private RoutingMode _routingMode = RoutingMode.Balanced;
    [ObservableProperty] private ThemeChoice? _selectedThemeChoice = ThemeChoices[0];
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ManualRouteCommand))]
    private ProviderRowViewModel? _selectedProvider;

    public ObservableCollection<ProviderRowViewModel> Providers { get; } = [];
    public ObservableCollection<GroupRowViewModel> Groups { get; } = [];
    public ObservableCollection<KeyRowViewModel> Keys { get; } = [];

    public string MultiplierHeader => SortHeader("倍率", "Multiplier");
    public string LatencyHeader => SortHeader("首字", "Latency");
    public string SuccessRateHeader => SortHeader("6h 可用率", "SuccessRate");
    public string WeightedScoreHeader => SortHeader("加权得分", "WeightedScore");
    public bool CanChangeRoutingMode => !IsBusy;

    public bool IsEconomy
    {
        get => RoutingMode == RoutingMode.Economy;
        set { if (value) RoutingMode = RoutingMode.Economy; }
    }

    public bool IsBalanced
    {
        get => RoutingMode == RoutingMode.Balanced;
        set { if (value) RoutingMode = RoutingMode.Balanced; }
    }

    public bool IsSpeed
    {
        get => RoutingMode == RoutingMode.Speed;
        set { if (value) RoutingMode = RoutingMode.Speed; }
    }

    public MainWindowViewModel()
    {
        Load();
    }

    partial void OnRoutingModeChanged(RoutingMode value)
    {
        OnPropertyChanged(nameof(IsEconomy));
        OnPropertyChanged(nameof(IsBalanced));
        OnPropertyChanged(nameof(IsSpeed));
        ConnectionSummary = $"API-only / {value}";
        _routingSettingsStale = true;
        _routingSettingsVersion++;
        SelectedProvider = null;
        Providers.Clear();
        CandidateSummary = "目标分组：请刷新后计算";
    }

    partial void OnAutoRoutingChanged(bool value)
    {
        if (value)
        {
            _ = StartAutoRoutingAsync();
        }
        else
        {
            StopAutoRouting();
        }
    }

    partial void OnSelectedThemeChoiceChanged(ThemeChoice? value)
    {
        if (value is null)
        {
            return;
        }

        if (Application.Current is { } application)
        {
            application.RequestedThemeVariant = value.Mode switch
            {
                AppThemeMode.Light => ThemeVariant.Light,
                AppThemeMode.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default
            };
        }

        try
        {
            var snapshot = _store.Load();
            if (snapshot.Settings.ThemeMode != value.Mode)
            {
                _store.Save(snapshot.Settings with { ThemeMode = value.Mode }, snapshot.Credentials);
            }
        }
        catch (Exception exception)
        {
            SetStatus(GetSafeMessage(exception), success: false);
        }
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            SaveSettings();
            ResetService();
            SetStatus("配置已保存。", success: true);
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, success: false);
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RunCycleAsync(dryRun: true, forceRefresh: true);
    }

    [RelayCommand]
    private async Task DryRunAsync()
    {
        await RunCycleAsync(dryRun: true, forceRefresh: false);
    }

    [RelayCommand]
    private async Task RouteAsync()
    {
        await RunCycleAsync(dryRun: false, forceRefresh: false);
    }

    [RelayCommand]
    private void VisitSite()
    {
        if (!Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri) ||
            (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus("站点地址无效，仅支持 HTTP 或 HTTPS。", success: false);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
            SetStatus("已在默认浏览器中打开站点。", success: true);
        }
        catch (Exception exception)
        {
            SetStatus(GetSafeMessage(exception), success: false);
        }
    }

    [RelayCommand]
    private void SortProviders(string? field)
    {
        if (field is not ("Multiplier" or "Latency" or "SuccessRate" or "WeightedScore"))
        {
            return;
        }

        if (_providerSortField == field)
        {
            _providerSortDescending = !_providerSortDescending;
        }
        else
        {
            _providerSortField = field;
            _providerSortDescending = field is "SuccessRate" or "WeightedScore";
        }

        SortProviderRows();
        OnPropertyChanged(nameof(MultiplierHeader));
        OnPropertyChanged(nameof(LatencyHeader));
        OnPropertyChanged(nameof(SuccessRateHeader));
        OnPropertyChanged(nameof(WeightedScoreHeader));
    }

    private bool CanManualRoute() =>
        !IsBusy && SelectedProvider is { CanManualRoute: true, GroupIdValue: > 0 };

    [RelayCommand(CanExecute = nameof(CanManualRoute))]
    private async Task ManualRouteAsync()
    {
        if (SelectedProvider is not { CanManualRoute: true, GroupIdValue: > 0 } selected)
        {
            return;
        }

        AutoRouting = false;
        IsBusy = true;
        try
        {
            SaveSettings();
            ResetService();
            EnsureService();
            var result = await _service!.RouteManuallyAsync(
                selected.GroupIdValue.Value,
                forceAccountRefresh: true);
            ApplyManualResult(result, selected);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetStatus(GetSafeMessage(exception), success: false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StartAutoRoutingAsync()
    {
        StopAutoRouting();
        _autoRoutingCancellation = new CancellationTokenSource();
        try
        {
            SaveSettings();
            ResetService();
            var interval = TimeSpan.FromSeconds((double)Math.Clamp(PollingIntervalSeconds, 30, 3600));
            using var timer = new PeriodicTimer(interval);
            do
            {
                await RunCycleAsync(dryRun: false, forceRefresh: false, _autoRoutingCancellation.Token);
            }
            while (await timer.WaitForNextTickAsync(_autoRoutingCancellation.Token));
        }
        catch (OperationCanceledException)
        {
            SetStatus("自动路由已停止。", success: true);
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, success: false);
            AutoRouting = false;
        }
    }

    private void StopAutoRouting()
    {
        _autoRoutingCancellation?.Cancel();
        _autoRoutingCancellation?.Dispose();
        _autoRoutingCancellation = null;
    }

    private async Task RunCycleAsync(
        bool dryRun,
        bool forceRefresh,
        CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        var settingsVersion = _routingSettingsVersion;
        try
        {
            EnsureService();
            var result = await _service!.RunOnceAsync(dryRun, forceRefresh, cancellationToken);
            if (settingsVersion == _routingSettingsVersion)
            {
                ApplyResult(result);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetStatus(GetSafeMessage(exception), success: false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void EnsureService()
    {
        if (_service is not null && !_routingSettingsStale)
        {
            return;
        }

        ResetService();
        SaveSettings();
        _profileLock = ProfileLock.TryAcquire(_store.StorageDirectory)
            ?? throw new InvalidOperationException("另一个 AIHubRouter 实例正在使用当前 profile。" );
        var snapshot = _store.Load();
        var credentials = snapshot.Credentials ?? BuildCredentials();
        _service = new RoutingService(
            snapshot.Settings,
            credentials,
            new JsonRouteStateStore(_store.StorageDirectory),
            persistCredentials: (updated, token) =>
            {
                token.ThrowIfCancellationRequested();
                _loadedCredentials = updated;
                BearerToken = updated.BearerToken;
                if (snapshot.Settings.PersistCredentials)
                {
                    _store.Save(snapshot.Settings, updated);
                }

                return Task.CompletedTask;
            });
        _routingSettingsStale = false;
    }

    private void ApplyResult(RoutingCycleResult result)
    {
        SelectedProvider = null;
        var persistedBlacklist = _store.Load().Settings.BlacklistedGroupIds.ToHashSet();
        var previousBlacklist = Groups.ToDictionary(group => group.Id, group => group.Blacklisted);
        Groups.Clear();
        foreach (var group in result.Groups
                     .Where(group => group.Platform.Equals(
                         RoutingModePlatform(),
                         StringComparison.OrdinalIgnoreCase))
                     .GroupBy(group => group.Id)
                     .Select(group => group.First())
                     .OrderBy(group => group.Id))
        {
            var blacklisted = previousBlacklist.TryGetValue(group.Id, out var previous)
                ? previous
                : persistedBlacklist.Contains(group.Id);
            Groups.Add(new GroupRowViewModel(group, blacklisted));
        }

        var blacklistedIds = Groups
            .Where(group => group.Blacklisted)
            .Select(group => group.Id)
            .ToHashSet();
        Providers.Clear();
        var targetId = result.Decision.Target?.Group.Id;
        var groups = result.Groups.ToDictionary(group => group.Id);
        foreach (var provider in result.Providers
                     .Where(provider => provider.Platform.Equals(
                         RoutingModePlatform(),
                         StringComparison.OrdinalIgnoreCase))
                     .OrderBy(provider => provider.GroupId == targetId ? 0 : 1)
                     .ThenBy(provider => provider.PriceMultiplier))
        {
            Providers.Add(new ProviderRowViewModel(
                provider,
                groups,
                targetId,
                result.Evaluation,
                blacklistedIds));
        }

        SortProviderRows();

        Keys.Clear();
        var selected = result.SelectedKeyIds.ToHashSet();
        foreach (var key in result.Keys)
        {
            Keys.Add(new KeyRowViewModel(key, selected.Contains(key.Id)));
        }

        if (result.Decision.Target is { } target)
        {
            var planName = string.IsNullOrWhiteSpace(target.Provider.PlanType)
                ? target.Group.Name
                : target.Provider.PlanType;
            CandidateSummary = $"目标分组：{target.Group.Id} / 方案：{planName} / {target.EffectiveMultiplier:0.####}x / {FormatLatency(target.Provider.FirstTokenLatencyMs)}";
        }
        else
        {
            CandidateSummary = "目标分组：无可用候选";
        }

        SetStatus(
            $"{result.Decision.Reason}；切换 {result.ChangedKeyCount} 个，失败 {result.FailedKeyCount} 个。",
            result.FailedKeyCount == 0);
    }

    private void ApplyManualResult(ManualRoutingResult result, ProviderRowViewModel selected)
    {
        Keys.Clear();
        var selectedKeyIds = result.SelectedKeyIds.ToHashSet();
        foreach (var key in result.Keys)
        {
            Keys.Add(new KeyRowViewModel(key, selectedKeyIds.Contains(key.Id)));
        }

        CandidateSummary = $"目标分组：{selected.GroupId} / 方案：{selected.Plan} / {selected.Multiplier} / {selected.Latency}";
        SetStatus(
            $"手动路由完成；切换 {result.ChangedKeyCount} 个，失败 {result.FailedKeyCount} 个。自动路由已关闭。",
            result.FailedKeyCount == 0);
    }

    private void SortProviderRows()
    {
        if (_providerSortField is null || Providers.Count < 2)
        {
            return;
        }

        Func<ProviderRowViewModel, double?> selector = _providerSortField switch
        {
            "Multiplier" => row => row.MultiplierValue,
            "Latency" => row => row.LatencyValue,
            "SuccessRate" => row => row.SuccessRateValue,
            "WeightedScore" => row => row.WeightedScoreValue,
            _ => row => row.MultiplierValue
        };
        var selected = SelectedProvider;
        var ordered = (_providerSortDescending
                ? Providers.OrderBy(row => selector(row) is null)
                    .ThenByDescending(selector)
                : Providers.OrderBy(row => selector(row) is null)
                    .ThenBy(selector))
            .ThenBy(row => row.GroupIdValue)
            .ThenBy(row => row.Plan, StringComparer.CurrentCulture)
            .ToArray();

        for (var index = 0; index < ordered.Length; index++)
        {
            Providers.Move(Providers.IndexOf(ordered[index]), index);
        }

        SelectedProvider = selected;
    }

    private string SortHeader(string label, string field) =>
        _providerSortField == field
            ? $"{label} {(_providerSortDescending ? "↓" : "↑")}"
            : label;

    private void Load()
    {
        try
        {
            var snapshot = _store.Load();
            var settings = snapshot.Settings;
            _loadedCredentials = snapshot.Credentials ?? new PersistentCredentials();
            BaseUrl = settings.BaseUrl;
            RoutingMode = settings.RoutingMode;
            PollingIntervalSeconds = settings.PollingIntervalSeconds;
            PersistCredentials = settings.PersistCredentials;
            SelectedThemeChoice = ThemeChoices.FirstOrDefault(choice => choice.Mode == settings.ThemeMode)
                ?? ThemeChoices[0];
            Email = _loadedCredentials.Email;
            Password = _loadedCredentials.Password;
            BearerToken = _loadedCredentials.BearerToken;
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, success: false);
        }
    }

    private void SaveSettings()
    {
        var selectedIds = Keys.Where(key => key.Selected).Select(key => key.Id).ToArray();
        var existing = _store.Load().Settings;
        var loadedGroupIds = Groups.Select(group => group.Id).ToHashSet();
        var blacklistedGroupIds = Groups
            .Where(group => group.Blacklisted)
            .Select(group => group.Id)
            .Concat(existing.BlacklistedGroupIds.Where(groupId => !loadedGroupIds.Contains(groupId)))
            .Distinct()
            .Order()
            .ToArray();
        var settings = existing with
        {
            BaseUrl = BaseUrl.Trim(),
            RoutingMode = RoutingMode,
            PollingIntervalSeconds = (int)PollingIntervalSeconds,
            PersistCredentials = PersistCredentials,
            ThemeMode = SelectedThemeChoice?.Mode ?? AppThemeMode.System,
            KeySelectionInitialized = Keys.Count > 0 || existing.KeySelectionInitialized,
            SelectedKeyIds = Keys.Count > 0 ? selectedIds : existing.SelectedKeyIds,
            BlacklistedGroupIds = Groups.Count > 0 ? blacklistedGroupIds : existing.BlacklistedGroupIds
        };
        var credentials = BuildCredentials();
        if (PersistCredentials && !_store.CanPersistCredentials)
        {
            throw new InvalidOperationException(_store.CredentialProtection);
        }

        _store.Save(settings, PersistCredentials ? credentials : null);
        _loadedCredentials = credentials;
    }

    private PersistentCredentials BuildCredentials()
    {
        return _loadedCredentials with
        {
            Email = Email.Trim(),
            Password = Password,
            BearerToken = BearerToken
        };
    }

    private void ResetService()
    {
        _service?.Dispose();
        _service = null;
        _profileLock?.Dispose();
        _profileLock = null;
    }

    private string RoutingModePlatform()
    {
        return _store.Load().Settings.Platform;
    }

    private void SetStatus(string message, bool success)
    {
        Status = message;
        StatusIsSuccess = success;
        StatusIsError = !success;
    }

    private static string GetSafeMessage(Exception exception)
    {
        return exception switch
        {
            AIHubApiException api => api.Message,
            HttpRequestException => "网络连接失败。",
            TaskCanceledException => "请求超时。",
            InvalidOperationException invalid => invalid.Message,
            ArgumentException argument => argument.Message,
            _ => "操作失败。"
        };
    }

    private static string FormatLatency(double? latency) =>
        latency is >= 0 && double.IsFinite(latency.Value) ? $"{latency:0} ms" : "未知";

    public void Dispose()
    {
        StopAutoRouting();
        ResetService();
    }
}

public sealed record ThemeChoice(AppThemeMode Mode, string Name);

public sealed record ProviderRowViewModel
{
    public ProviderRowViewModel(
        ProviderStatus provider,
        IReadOnlyDictionary<long, GroupInfo> groups,
        long? targetGroupId,
        RouteEvaluation evaluation,
        IReadOnlySet<long> blacklistedGroupIds)
    {
        var candidate = evaluation.EligibleCandidates.FirstOrDefault(item =>
            item.Group.Id == provider.GroupId &&
            item.Provider.Id.Equals(provider.Id, StringComparison.Ordinal));
        var effectiveMultiplier = candidate?.EffectiveMultiplier ?? provider.PriceMultiplier;
        var weightedScore = candidate is null
            ? null
            : RoutingEngine.CalculateWeightedScore(evaluation, candidate);

        GroupIdValue = provider.GroupId;
        GroupId = provider.GroupId?.ToString() ?? "-";
        Plan = string.IsNullOrWhiteSpace(provider.PlanType) &&
               provider.GroupId is { } groupId &&
               groups.TryGetValue(groupId, out var group)
            ? group.Name
            : provider.PlanType;
        MultiplierValue = double.IsFinite(effectiveMultiplier) ? effectiveMultiplier : null;
        Multiplier = MultiplierValue is { } multiplier ? $"{multiplier:0.####}x" : "-";
        LatencyValue = provider.FirstTokenLatencyMs is >= 0 and var latency && double.IsFinite(latency)
            ? latency
            : null;
        Latency = LatencyValue is { } latencyValue
            ? $"{latencyValue:0} ms"
            : "-";
        SuccessRateValue = provider.SuccessRate6h;
        SuccessRate = provider.SuccessRate6h is { } success ? $"{success:P1}" : "-";
        WeightedScoreValue = weightedScore;
        WeightedScore = weightedScore is { } score
            ? score.ToString("+0.0000;-0.0000;0.0000")
            : "-";
        State = provider.GroupId is { } stateGroupId && blacklistedGroupIds.Contains(stateGroupId)
            ? "黑名单"
            : provider.GroupId == targetGroupId
            ? "推荐"
            : !provider.Enabled ? "停用"
            : !provider.Available ? "异常"
            : provider.HasWarnings ? "警告"
            : "可用";
        CheckedAt = provider.CheckedAt?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "-";
        CanManualRoute = provider.GroupId is { } manualGroupId &&
            groups.TryGetValue(manualGroupId, out var manualGroup) &&
            manualGroup.Status.Equals("active", StringComparison.OrdinalIgnoreCase) &&
            !blacklistedGroupIds.Contains(manualGroupId);
    }

    public long? GroupIdValue { get; }
    public string GroupId { get; }
    public string Plan { get; }
    public double? MultiplierValue { get; }
    public string Multiplier { get; }
    public double? LatencyValue { get; }
    public string Latency { get; }
    public double? SuccessRateValue { get; }
    public string SuccessRate { get; }
    public double? WeightedScoreValue { get; }
    public string WeightedScore { get; }
    public string State { get; }
    public string CheckedAt { get; }
    public bool CanManualRoute { get; }
}

public sealed partial class GroupRowViewModel : ObservableObject
{
    [ObservableProperty] private bool _blacklisted;

    public GroupRowViewModel(GroupInfo group, bool blacklisted)
    {
        Id = group.Id;
        Name = group.Name;
        Platform = group.Platform;
        Status = group.Status;
        _blacklisted = blacklisted;
    }

    public long Id { get; }
    public string Name { get; }
    public string Platform { get; }
    public string Status { get; }
}

public sealed partial class KeyRowViewModel : ObservableObject
{
    [ObservableProperty] private bool _selected;

    public KeyRowViewModel(ApiKeyInfo key, bool selected)
    {
        Id = key.Id;
        Name = key.Name;
        Status = key.Status;
        GroupId = key.GroupId?.ToString() ?? "-";
        GroupName = key.Group?.Name ?? "未绑定";
        _selected = selected;
    }

    public long Id { get; }
    public string Name { get; }
    public string Status { get; }
    public string GroupId { get; }
    public string GroupName { get; }
}
