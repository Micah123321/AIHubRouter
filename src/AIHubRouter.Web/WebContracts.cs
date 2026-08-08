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
    long[] BlacklistedGroupIds);

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
    DateTimeOffset? LastUpdatedAt);

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
    string CredentialProtection,
    AppThemeMode ThemeMode,
    long[] SelectedKeyIds,
    long[] BlacklistedGroupIds);

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
    bool Recommended);

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
    bool Selected);
