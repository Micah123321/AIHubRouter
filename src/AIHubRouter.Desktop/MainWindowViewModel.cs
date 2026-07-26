using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
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
    private CancellationTokenSource? _manualMonitoringCancellation;
    private PersistentCredentials _loadedCredentials = new();
    private string? _providerSortField = "WeightedScore";
    private bool _providerSortDescending = true;
    private RoutingCycleResult? _lastCycleResult;
    private long? _manualRouteGroupId;
    private string? _manualRoutePlan;
    private bool _routingSettingsStale;
    private int _routingSettingsVersion;

    [ObservableProperty] private string _baseUrl = "https://aihub.top";
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _bearerToken = string.Empty;
    [ObservableProperty] private decimal _groupStickiness =
        (decimal)BalancedRoutingPolicy.DefaultMinimumScoreAdvantageToSwitch;
    [ObservableProperty] private string _minimumPriceMultiplierText =
        FormatPriceMultiplier(BalancedRoutingPolicy.DefaultMinimumPriceMultiplier);
    [ObservableProperty] private string _maximumPriceMultiplierText =
        FormatPriceMultiplier(BalancedRoutingPolicy.DefaultMaximumPriceMultiplier);
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

    public string GroupHeader => SortHeader("分组", "GroupId");
    public string PlanHeader => SortHeader("方案", "Plan");
    public string MultiplierHeader => SortHeader("倍率", "Multiplier");
    public string LatencyHeader => SortHeader("首字", "Latency");
    public string ConfidenceHeader => SortHeader("置信度 / 样本", "Confidence");
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

    partial void OnGroupStickinessChanged(decimal value)
    {
        MarkRoutingSettingsChanged();
    }

    partial void OnMinimumPriceMultiplierTextChanged(string value)
    {
        MarkRoutingSettingsChanged();
    }

    partial void OnMaximumPriceMultiplierTextChanged(string value)
    {
        MarkRoutingSettingsChanged();
    }

    private void MarkRoutingSettingsChanged()
    {
        _routingSettingsStale = true;
        _routingSettingsVersion++;
        CandidateSummary = "目标分组：请刷新后计算";
    }

    partial void OnAutoRoutingChanged(bool value)
    {
        if (value)
        {
            ExitManualRoutingMode();
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
        ExitManualRoutingMode();
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
        if (field is not ("GroupId" or "Plan" or "Multiplier" or "Latency" or "Confidence" or "WeightedScore"))
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
            _providerSortDescending = field is "Confidence" or "WeightedScore";
        }

        SortProviderRows();
        OnPropertyChanged(nameof(GroupHeader));
        OnPropertyChanged(nameof(PlanHeader));
        OnPropertyChanged(nameof(MultiplierHeader));
        OnPropertyChanged(nameof(LatencyHeader));
        OnPropertyChanged(nameof(ConfidenceHeader));
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
            var selectedKeyIds = Keys
                .Where(key => key.Selected)
                .Select(key => key.Id)
                .ToArray();
            SaveSettings();
            ResetService();
            EnsureService();
            var result = await _service!.RouteManuallyAsync(
                selected.GroupIdValue.Value,
                forceAccountRefresh: true,
                selectedKeyIds: selectedKeyIds);
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
            while (IsBusy)
            {
                await Task.Delay(100, _autoRoutingCancellation.Token);
            }

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

    private void StartManualMonitoring()
    {
        StopManualMonitoring();
        _manualMonitoringCancellation = new CancellationTokenSource();
        _ = MonitorManualRouteAsync(_manualMonitoringCancellation.Token);
    }

    private async Task MonitorManualRouteAsync(CancellationToken cancellationToken)
    {
        try
        {
            var interval = TimeSpan.FromSeconds((double)Math.Clamp(PollingIntervalSeconds, 30, 3600));
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await RunCycleAsync(dryRun: true, forceRefresh: false, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void StopManualMonitoring()
    {
        _manualMonitoringCancellation?.Cancel();
        _manualMonitoringCancellation?.Dispose();
        _manualMonitoringCancellation = null;
    }

    private void ExitManualRoutingMode()
    {
        _manualRouteGroupId = null;
        _manualRoutePlan = null;
        StopManualMonitoring();
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
            var result = await _service!.RunOnceAsync(
                dryRun,
                forceRefresh,
                cancellationToken,
                CaptureSelectedKeyIds());
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
        _lastCycleResult = result;
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
            var row = new GroupRowViewModel(group, blacklisted);
            row.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(GroupRowViewModel.Blacklisted))
                {
                    _routingSettingsStale = true;
                    _routingSettingsVersion++;
                    RecalculateProviderScores();
                    ManualRouteCommand.NotifyCanExecuteChanged();
                }
            };
            Groups.Add(row);
        }

        Providers.Clear();
        var targetId = result.Decision.Target?.Group.Id;
        var groups = result.Groups.ToDictionary(group => group.Id);
        var groupRows = Groups.ToDictionary(group => group.Id);
        foreach (var provider in result.Providers
                     .Where(provider => provider.Platform.Equals(
                         RoutingModePlatform(),
                         StringComparison.OrdinalIgnoreCase))
                     .OrderBy(provider => provider.GroupId == targetId ? 0 : 1)
                     .ThenBy(provider => provider.PriceMultiplier))
        {
            var row = new ProviderRowViewModel(
                provider,
                groups,
                groupRows,
                targetId,
                _manualRouteGroupId,
                result.Evaluation);
            row.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ProviderRowViewModel.CanManualRoute))
                {
                    ManualRouteCommand.NotifyCanExecuteChanged();
                }
            };
            Providers.Add(row);
        }

        SortProviderRows();

        Keys.Clear();
        var selected = result.SelectedKeyIds.ToHashSet();
        foreach (var key in result.Keys)
        {
            Keys.Add(CreateKeyRow(key, selected.Contains(key.Id)));
        }

        if (_manualRouteGroupId is { } manualGroupId)
        {
            var selectedKeyIds = result.SelectedKeyIds.ToHashSet();
            var actualGroupIds = result.Keys
                .Where(key => selectedKeyIds.Contains(key.Id))
                .Select(key => key.GroupId)
                .Where(groupId => groupId is > 0)
                .Distinct()
                .ToArray();
            var stillRoutedManually = actualGroupIds.Length == 1 && actualGroupIds[0] == manualGroupId;
            var manualGroupAvailable = result.Evaluation.EligibleCandidates.Any(candidate =>
                candidate.Group.Id == manualGroupId);

            if (stillRoutedManually && manualGroupAvailable)
            {
                UpdateManualCandidateSummary(manualGroupId);
                SetStatus($"手动路由监控正常；分组 {manualGroupId} 可用。", success: true);
                return;
            }

            ExitManualRoutingMode();
            UpdateAutomaticCandidateSummary(result);
            SetStatus("手动分组状态异常或已不再生效，已启用自动路由。", success: true);
            AutoRouting = true;
            return;
        }

        UpdateAutomaticCandidateSummary(result);

        SetStatus(
            $"{result.Decision.Reason}；切换 {result.ChangedKeyCount} 个，失败 {result.FailedKeyCount} 个。",
            result.FailedKeyCount == 0);
    }

    private void RecalculateProviderScores()
    {
        if (_lastCycleResult is not { } result)
        {
            return;
        }

        var settings = _store.Load().Settings;
        var policy = settings.CreatePolicy() with
        {
            Mode = RoutingMode,
            BlacklistedGroupIds = Groups
                .Where(group => group.Blacklisted)
                .Select(group => group.Id)
                .ToArray()
        };
        var evaluation = RoutingEngine.Evaluate(
            result.Providers,
            result.Groups,
            result.UserGroupRates,
            policy,
            result.CompletedAt);

        foreach (var provider in Providers)
        {
            provider.ApplyEvaluation(evaluation);
        }

        SortProviderRows();
    }

    private void ApplyManualResult(ManualRoutingResult result, ProviderRowViewModel selected)
    {
        Keys.Clear();
        var selectedKeyIds = result.SelectedKeyIds.ToHashSet();
        foreach (var key in result.Keys)
        {
            Keys.Add(CreateKeyRow(key, selectedKeyIds.Contains(key.Id)));
        }

        var actualGroupIds = result.Keys
            .Where(key => selectedKeyIds.Contains(key.Id))
            .Select(key => key.GroupId)
            .Where(groupId => groupId is > 0)
            .Distinct()
            .ToArray();
        if (result.FailedKeyCount > 0 ||
            actualGroupIds.Length != 1 ||
            actualGroupIds[0] != selected.GroupIdValue)
        {
            ExitManualRoutingMode();
            SetStatus(
                $"手动路由未完全生效；切换 {result.ChangedKeyCount} 个，失败 {result.FailedKeyCount} 个。已启用自动路由。",
                success: false);
            AutoRouting = true;
            return;
        }

        _manualRouteGroupId = selected.GroupIdValue;
        _manualRoutePlan = selected.Plan;
        UpdateManualCandidateSummary(selected.GroupIdValue!.Value);
        StartManualMonitoring();
        SetStatus(
            $"手动路由完成；切换 {result.ChangedKeyCount} 个，失败 {result.FailedKeyCount} 个。自动路由已关闭。",
            result.FailedKeyCount == 0);
    }

    private void UpdateManualCandidateSummary(long groupId)
    {
        var provider = Providers.FirstOrDefault(row =>
                row.GroupIdValue == groupId &&
                row.Plan.Equals(_manualRoutePlan, StringComparison.CurrentCulture))
            ?? Providers.FirstOrDefault(row => row.GroupIdValue == groupId);
        CandidateSummary = provider is null
            ? $"目标分组：{groupId}（手动） / 方案：{_manualRoutePlan ?? "-"}"
            : $"目标分组：{provider.GroupId}（手动） / 方案：{provider.Plan} / {provider.Multiplier} / {provider.Latency}";
    }

    private void UpdateAutomaticCandidateSummary(RoutingCycleResult result)
    {
        if (result.Decision.Target is not { } target)
        {
            CandidateSummary = "目标分组：无可用候选";
            return;
        }

        var planName = string.IsNullOrWhiteSpace(target.Provider.PlanType)
            ? target.Group.Name
            : target.Provider.PlanType;
        CandidateSummary = $"目标分组：{target.Group.Id} / 方案：{planName} / {target.EffectiveMultiplier:0.####}x / {FormatLatency(target.Provider.FirstTokenLatencyMs)}";
    }

    private void SortProviderRows()
    {
        if (_providerSortField is null || Providers.Count < 2)
        {
            return;
        }

        var selected = SelectedProvider;
        IOrderedEnumerable<ProviderRowViewModel> ordered = _providerSortField switch
        {
            "Plan" => _providerSortDescending
                ? Providers.OrderByDescending(row => row.Plan, StringComparer.CurrentCulture)
                : Providers.OrderBy(row => row.Plan, StringComparer.CurrentCulture),
            "GroupId" => OrderNumeric(row => row.GroupIdValue),
            "Multiplier" => OrderNumeric(row => row.MultiplierValue),
            "Latency" => OrderNumeric(row => row.LatencyValue),
            "Confidence" => OrderNumeric(row => row.ConfidenceValue),
            "WeightedScore" => OrderNumeric(row => row.WeightedScoreValue),
            _ => OrderNumeric(row => row.WeightedScoreValue)
        };
        var sorted = ordered
            .ThenBy(row => row.GroupIdValue)
            .ThenBy(row => row.Plan, StringComparer.CurrentCulture)
            .ToArray();

        for (var index = 0; index < sorted.Length; index++)
        {
            Providers.Move(Providers.IndexOf(sorted[index]), index);
        }

        SelectedProvider = selected;

        IOrderedEnumerable<ProviderRowViewModel> OrderNumeric(
            Func<ProviderRowViewModel, double?> selector) =>
            _providerSortDescending
                ? Providers.OrderBy(row => selector(row) is null).ThenByDescending(selector)
                : Providers.OrderBy(row => selector(row) is null).ThenBy(selector);
    }

    private string SortHeader(string label, string field) =>
        _providerSortField == field
            ? $"{label} {(_providerSortDescending ? "↓" : "↑")}"
            : label;

    private long[]? CaptureSelectedKeyIds() =>
        Keys.Count == 0
            ? null
            : Keys.Where(key => key.Selected).Select(key => key.Id).ToArray();

    private KeyRowViewModel CreateKeyRow(ApiKeyInfo key, bool selected)
    {
        var row = new KeyRowViewModel(key, selected);
        row.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(KeyRowViewModel.Selected))
            {
                _routingSettingsStale = true;
                _routingSettingsVersion++;
            }
        };
        return row;
    }

    private void Load()
    {
        try
        {
            var snapshot = _store.Load();
            var settings = snapshot.Settings;
            _loadedCredentials = snapshot.Credentials ?? new PersistentCredentials();
            BaseUrl = settings.BaseUrl;
            RoutingMode = settings.RoutingMode;
            GroupStickiness = (decimal)settings.CreatePolicy().MinimumScoreAdvantageToSwitch;
            MinimumPriceMultiplierText = FormatPriceMultiplier(settings.MinimumPriceMultiplier);
            MaximumPriceMultiplierText = FormatPriceMultiplier(settings.MaximumPriceMultiplier);
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
        var (minimumPriceMultiplier, maximumPriceMultiplier) = ParsePriceRange();
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
            GroupStickiness = (double)GroupStickiness,
            MinimumPriceMultiplier = minimumPriceMultiplier,
            MaximumPriceMultiplier = maximumPriceMultiplier,
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

    private (double Minimum, double Maximum) ParsePriceRange()
    {
        if (!TryParsePriceMultiplier(MinimumPriceMultiplierText, out var minimum) ||
            !TryParsePriceMultiplier(MaximumPriceMultiplierText, out var maximum) ||
            minimum > maximum)
        {
            throw new ArgumentException("价格范围必须是非负有限数值，且最小值不能大于最大值。");
        }

        return (minimum, maximum);
    }

    private static bool TryParsePriceMultiplier(string text, out double value)
    {
        var normalized = text.Trim().Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               double.IsFinite(value) &&
               value >= 0;
    }

    private static string FormatPriceMultiplier(double value) =>
        value.ToString("0.################", CultureInfo.InvariantCulture);

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
        StopManualMonitoring();
        StopAutoRouting();
        ResetService();
    }
}

public sealed record ThemeChoice(AppThemeMode Mode, string Name);

public sealed class ProviderRowViewModel : ObservableObject
{
    private readonly GroupRowViewModel? _group;
    private readonly ProviderStatus _provider;
    private readonly string _baseState;
    private readonly bool _baseCanManualRoute;
    private double? _weightedScoreValue;
    private string _weightedScore = "-";

    public ProviderRowViewModel(
        ProviderStatus provider,
        IReadOnlyDictionary<long, GroupInfo> groups,
        IReadOnlyDictionary<long, GroupRowViewModel> groupRows,
        long? targetGroupId,
        long? manualTargetGroupId,
        RouteEvaluation evaluation)
    {
        _provider = provider;
        var candidate = FindCandidate(evaluation);
        var effectiveMultiplier = candidate?.EffectiveMultiplier ?? provider.PriceMultiplier;

        GroupIdValue = provider.GroupId;
        _group = provider.GroupId is { } sharedGroupId && groupRows.TryGetValue(sharedGroupId, out var groupRow)
            ? groupRow
            : null;
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
        ConfidenceValue = provider.LatencyConfidence;
        Confidence = provider.LatencyConfidence is { } confidence
            ? $"{confidence:P0} / {provider.UsageSampleCount}"
            : "-";
        ApplyEvaluation(evaluation);
        _baseState = !provider.Enabled ? "停用"
            : !provider.Available ? "异常"
            : provider.HasWarnings ? "警告"
            : provider.GroupId == manualTargetGroupId ? "手动"
            : provider.GroupId == targetGroupId ? "推荐"
            : "可用";
        CheckedAt = provider.CheckedAt?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "-";
        _baseCanManualRoute = provider.GroupId is { } manualGroupId &&
            groups.TryGetValue(manualGroupId, out var manualGroup) &&
            manualGroup.Status.Equals("active", StringComparison.OrdinalIgnoreCase);

        if (_group is not null)
        {
            _group.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(GroupRowViewModel.Blacklisted))
                {
                    OnPropertyChanged(nameof(Blacklisted));
                    OnPropertyChanged(nameof(BlacklistToolTip));
                    OnPropertyChanged(nameof(State));
                    OnPropertyChanged(nameof(CanManualRoute));
                }
            };
        }
    }

    public long? GroupIdValue { get; }
    public string GroupId { get; }
    public string Plan { get; }
    public double? MultiplierValue { get; }
    public string Multiplier { get; }
    public double? LatencyValue { get; }
    public string Latency { get; }
    public double? ConfidenceValue { get; }
    public string Confidence { get; }
    public double? WeightedScoreValue => _weightedScoreValue;
    public string WeightedScore => _weightedScore;
    public bool Blacklisted
    {
        get => _group?.Blacklisted ?? false;
        set
        {
            if (_group is not null)
            {
                _group.Blacklisted = value;
            }
        }
    }
    public string BlacklistToolTip => Blacklisted ? "已禁用，点击恢复候选" : "点击禁用此分组";
    public string State => Blacklisted ? "黑名单" : _baseState;
    public string CheckedAt { get; }
    public bool CanManualRoute => _baseCanManualRoute && !Blacklisted;

    public void ApplyEvaluation(RouteEvaluation evaluation)
    {
        var candidate = FindCandidate(evaluation);
        var score = candidate is null
            ? null
            : RoutingEngine.CalculateWeightedScore(evaluation, candidate);
        var formatted = score is { } value
            ? value.ToString("+0.0000;-0.0000;0.0000")
            : "-";

        if (_weightedScoreValue == score && _weightedScore == formatted)
        {
            return;
        }

        _weightedScoreValue = score;
        _weightedScore = formatted;
        OnPropertyChanged(nameof(WeightedScoreValue));
        OnPropertyChanged(nameof(WeightedScore));
    }

    private RouteCandidate? FindCandidate(RouteEvaluation evaluation) =>
        evaluation.EligibleCandidates.FirstOrDefault(item =>
            item.Group.Id == _provider.GroupId &&
            item.Provider.Id.Equals(_provider.Id, StringComparison.Ordinal));
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
