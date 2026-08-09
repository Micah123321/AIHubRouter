using AIHubRouter.Core;

namespace AIHubRouter.Web;

public sealed record LoginRequest(string? Password);

public sealed record SettingsUpdateRequest(
    string BaseUrl,
    string Email,
    string? Password,
    string? BearerToken,
    bool ClearPassword,
    bool ClearBearerToken,
    RoutingMode RoutingMode,
    double GroupStickiness,
    double MinimumPriceMultiplier,
    double MaximumPriceMultiplier,
    double ConfidenceImpact,
    double MinimumConfidence,
    double? ProviderSeriesWeight,
    int? ProviderSeriesCacheSeconds,
    string? ProviderSeriesRange,
    string? ProviderSeriesTimezone,
    int PollingIntervalSeconds,
    bool PersistCredentials,
    AppThemeMode ThemeMode,
    long[] SelectedKeyIds,
    long[] BlacklistedGroupIds,
    long[]? LunaSelectedKeyIds = null,
    bool? ReliabilityDetectionEnabled = null,
    int? ReliabilityDetectionIntervalSeconds = null,
    int? ReliabilityQuarantineHours = null,
    string? DetectorPythonCommand = null,
    string? DetectorWorkerPath = null,
    string? DetectorPreset = null,
    DetectorBinding[]? DetectorBindings = null,
    Dictionary<long, string>? DetectorApiKeys = null);

public sealed record ManualRouteRequest(long GroupId);
public sealed record AutoRoutingRequest(bool Enabled);

public sealed record WebDashboard(
    WebSettings Settings,
    IReadOnlyList<WebProviderRow> Providers,
    IReadOnlyList<WebGroupRow> Groups,
    IReadOnlyList<WebKeyRow> Keys,
    bool IsBusy,
    bool AutoRouting,
    string Status,
    string StatusKind,
    WebProviderSeriesStatus? ProviderSeriesStatus,
    WebProviderCacheHitRateStatus? ProviderCacheHitRateStatus,
    string CandidateSummary,
    string ConnectionSummary,
    DateTimeOffset? LastUpdatedAt)
{
    public WebLunaRoute? LunaRoute { get; init; }
    public ChannelReliabilityCycleResult? Reliability { get; init; }
}

public sealed record WebLunaRoute(
    bool Configured,
    bool HasRun,
    bool HealthAvailable,
    bool HasTarget,
    string HealthMessage,
    int FilteredGroupCount,
    int SelectedKeyCount,
    long? GroupId,
    string? Plan,
    double? Multiplier,
    double? Latency,
    string DecisionReason);

public sealed record WebSettings(
    string BaseUrl,
    string Email,
    bool HasPassword,
    bool HasBearerToken,
    RoutingMode RoutingMode,
    double GroupStickiness,
    double MinimumPriceMultiplier,
    double MaximumPriceMultiplier,
    double ConfidenceImpact,
    double MinimumConfidence,
    double ProviderSeriesWeight,
    int ProviderSeriesCacheSeconds,
    string ProviderSeriesRange,
    string ProviderSeriesTimezone,
    int PollingIntervalSeconds,
    bool PersistCredentials,
    bool CanPersistCredentials,
    bool CredentialsUnavailable,
    string CredentialProtection,
    AppThemeMode ThemeMode,
    long[] SelectedKeyIds,
    long[] LunaSelectedKeyIds,
    long[] BlacklistedGroupIds)
{
    public bool ReliabilityDetectionEnabled { get; init; }
    public int ReliabilityDetectionIntervalSeconds { get; init; } = 600;
    public int ReliabilityQuarantineHours { get; init; } = 24;
    public string DetectorPythonCommand { get; init; } = "python3";
    public string DetectorWorkerPath { get; init; } = "scripts/channel_detector_worker.py";
    public string DetectorPreset { get; init; } = "low";
    public IReadOnlyList<DetectorBinding> DetectorBindings { get; init; } = [];
}

public sealed record WebProviderSeriesStatus(
    bool Available,
    bool FromCache,
    bool IsDegraded,
    string Message);

public sealed record WebProviderCacheHitRateStatus(
    bool Available,
    bool FromCache,
    bool IsDegraded,
    string Message);

public sealed record WebProviderRow(
    string ProviderId,
    long? GroupId,
    string Plan,
    double? Multiplier,
    double? Latency,
    double? Confidence,
    double? CacheHitRate,
    int SampleCount,
    double? WeightedScore,
    string State,
    DateTimeOffset? CheckedAt,
    bool CanManualRoute,
    bool Recommended)
{
    public string ReliabilityState { get; init; } = "Unconfigured";
    public DateTimeOffset? ReliabilityQuarantinedUntil { get; init; }
    public IReadOnlyList<string> ReliabilityModels { get; init; } = [];
}

public sealed record WebGroupRow(
    long Id,
    string Name,
    string Platform,
    string Status,
    bool Blacklisted);

public sealed record WebKeyRow(
    long Id,
    string Name,
    string Status,
    long? GroupId,
    string GroupName,
    bool Selected,
    bool LunaSelected)
{
    public string ReliabilityState { get; init; } = "Unconfigured";
    public DateTimeOffset? ReliabilityQuarantinedUntil { get; init; }
    public IReadOnlyList<string> ReliabilityModels { get; init; } = [];
}
