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
    string CandidateSummary,
    string ConnectionSummary,
    DateTimeOffset? LastUpdatedAt);

public sealed record WebSettings(
    string BaseUrl,
    string Email,
    bool HasPassword,
    bool HasBearerToken,
    RoutingMode RoutingMode,
    int PollingIntervalSeconds,
    bool PersistCredentials,
    bool CanPersistCredentials,
    string CredentialProtection,
    AppThemeMode ThemeMode,
    long[] SelectedKeyIds,
    long[] BlacklistedGroupIds);

public sealed record WebProviderRow(
    string ProviderId,
    long? GroupId,
    string Plan,
    double? Multiplier,
    double? Latency,
    double? SuccessRate,
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
