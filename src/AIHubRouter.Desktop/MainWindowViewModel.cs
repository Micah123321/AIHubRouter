using System.Collections.ObjectModel;
using Avalonia.Media;
using AIHubRouter.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIHubRouter.Desktop;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly AppSettingsStore _store = new();
    private RoutingService? _service;
    private ProfileLock? _profileLock;
    private CancellationTokenSource? _autoRoutingCancellation;
    private PersistentCredentials _loadedCredentials = new();

    [ObservableProperty] private string _baseUrl = "https://aihub.top";
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _bearerToken = string.Empty;
    [ObservableProperty] private decimal _minimumSuccessPercent = 90;
    [ObservableProperty] private decimal _pollingIntervalSeconds = 60;
    [ObservableProperty] private bool _persistCredentials;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _autoRouting;
    [ObservableProperty] private string _status = "就绪";
    [ObservableProperty] private IBrush _statusColor = Brush.Parse("#3F4A56");
    [ObservableProperty] private string _candidateSummary = "目标分组：-";
    [ObservableProperty] private string _connectionSummary = "API-only / Balanced";
    [ObservableProperty] private RoutingMode _routingMode = RoutingMode.Balanced;

    public ObservableCollection<ProviderRowViewModel> Providers { get; } = [];
    public ObservableCollection<KeyRowViewModel> Keys { get; } = [];

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
        try
        {
            EnsureService();
            var result = await _service!.RunOnceAsync(dryRun, forceRefresh, cancellationToken);
            ApplyResult(result);
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
        if (_service is not null)
        {
            return;
        }

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
    }

    private void ApplyResult(RoutingCycleResult result)
    {
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
            Providers.Add(new ProviderRowViewModel(provider, groups, targetId));
        }

        Keys.Clear();
        var selected = result.SelectedKeyIds.ToHashSet();
        foreach (var key in result.Keys)
        {
            Keys.Add(new KeyRowViewModel(key, selected.Contains(key.Id)));
        }

        if (result.Decision.Target is { } target)
        {
            CandidateSummary = $"目标分组：{target.Group.Id} / {target.EffectiveMultiplier:0.####}x / {FormatLatency(target.Provider.FirstTokenLatencyMs)}";
        }
        else
        {
            CandidateSummary = "目标分组：无可用候选";
        }

        SetStatus(
            $"{result.Decision.Reason}；切换 {result.ChangedKeyCount} 个，失败 {result.FailedKeyCount} 个。",
            result.FailedKeyCount == 0);
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
            MinimumSuccessPercent = settings.MinimumSuccessPercent;
            PollingIntervalSeconds = settings.PollingIntervalSeconds;
            PersistCredentials = settings.PersistCredentials;
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
        var settings = existing with
        {
            BaseUrl = BaseUrl.Trim(),
            RoutingMode = RoutingMode,
            MinimumSuccessPercent = (int)MinimumSuccessPercent,
            PollingIntervalSeconds = (int)PollingIntervalSeconds,
            PersistCredentials = PersistCredentials,
            KeySelectionInitialized = Keys.Count > 0 || existing.KeySelectionInitialized,
            SelectedKeyIds = Keys.Count > 0 ? selectedIds : existing.SelectedKeyIds
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
        StatusColor = Brush.Parse(success ? "#12633E" : "#B3261E");
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

public sealed record ProviderRowViewModel
{
    public ProviderRowViewModel(
        ProviderStatus provider,
        IReadOnlyDictionary<long, GroupInfo> groups,
        long? targetGroupId)
    {
        GroupId = provider.GroupId?.ToString() ?? "-";
        Plan = provider.PlanType;
        Multiplier = $"{provider.PriceMultiplier:0.####}x";
        Latency = provider.FirstTokenLatencyMs is >= 0 and var latency && double.IsFinite(latency)
            ? $"{latency:0} ms"
            : "-";
        SuccessRate = provider.SuccessRate6h is { } success ? $"{success:P1}" : "-";
        State = provider.GroupId == targetGroupId
            ? "推荐"
            : provider.Enabled && provider.Available ? "可用" : "异常";
        CheckedAt = provider.CheckedAt?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "-";
    }

    public string GroupId { get; }
    public string Plan { get; }
    public string Multiplier { get; }
    public string Latency { get; }
    public string SuccessRate { get; }
    public string State { get; }
    public string CheckedAt { get; }
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
