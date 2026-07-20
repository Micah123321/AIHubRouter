using System.Text.Json.Serialization;

namespace AIHubRouter.Core;

public sealed class MonitorSummary
{
    [JsonPropertyName("apis")]
    public List<ProviderStatus> Apis { get; init; } = [];

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset? GeneratedAt { get; init; }

    [JsonPropertyName("monitoringActive")]
    public bool MonitoringActive { get; init; }
}

public sealed class ProviderStatus
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("group_id")]
    public long? GroupId { get; init; }

    [JsonPropertyName("planType")]
    public string PlanType { get; init; } = string.Empty;

    [JsonPropertyName("platform")]
    public string Platform { get; init; } = string.Empty;

    [JsonPropertyName("priceMultiplier")]
    public double PriceMultiplier { get; init; }

    [JsonPropertyName("available")]
    public bool Available { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("checkedAt")]
    public DateTimeOffset? CheckedAt { get; init; }

    [JsonPropertyName("firstTokenLatencyMs")]
    public double? FirstTokenLatencyMs { get; init; }

    [JsonPropertyName("outputTokensPerSecond")]
    public double? OutputTokensPerSecond { get; init; }

    [JsonPropertyName("successRates")]
    public Dictionary<string, double> SuccessRates { get; init; } = [];

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }

    public double? SuccessRate6h => SuccessRates.TryGetValue("6h", out var value) ? value : null;
}

public sealed class GroupInfo
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("platform")]
    public string Platform { get; init; } = string.Empty;

    [JsonPropertyName("rate_multiplier")]
    public double RateMultiplier { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;
}

public sealed class ApiKeyInfo
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("group_id")]
    public long? GroupId { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("group")]
    public GroupInfo? Group { get; init; }
}

public sealed class PaginatedResponse<T>
{
    [JsonPropertyName("items")]
    public List<T> Items { get; init; } = [];

    [JsonPropertyName("total")]
    public int Total { get; init; }

    [JsonPropertyName("page")]
    public int Page { get; init; }

    [JsonPropertyName("page_size")]
    public int PageSize { get; init; }

    [JsonPropertyName("pages")]
    public int Pages { get; init; }
}

public sealed record RoutingCriteria(
    string Platform,
    double MinimumSuccessRate6h,
    TimeSpan MaximumStatusAge);

public sealed record RouteCandidate(
    ProviderStatus Provider,
    GroupInfo Group,
    double EffectiveMultiplier,
    bool HasUserRateOverride);

public enum RoutingMode
{
    Economy,
    Balanced,
    Speed
}

public sealed record BalancedRoutingPolicy
{
    public string Platform { get; init; } = "openai";
    public RoutingMode Mode { get; init; } = RoutingMode.Balanced;
    public double MinimumSuccessRate6h { get; init; } = 0.9;
    public TimeSpan MaximumStatusAge { get; init; } = TimeSpan.FromMinutes(15);
    public double? MaximumPricePremiumPercent { get; init; }
    public double MinimumPriceImprovementPercent { get; init; } = 5;
    public double MinimumLatencyImprovementPercent { get; init; } = 15;
    public int RequiredConfirmations { get; init; } = 2;
    public TimeSpan MinimumDwellTime { get; init; } = TimeSpan.FromMinutes(5);

    public double PricePremiumPercent => MaximumPricePremiumPercent ?? Mode switch
    {
        RoutingMode.Economy => 5,
        RoutingMode.Balanced => 15,
        RoutingMode.Speed => 30,
        _ => 15
    };

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Platform))
        {
            throw new ArgumentException("路由平台不能为空。", nameof(Platform));
        }

        if (MinimumSuccessRate6h is < 0 or > 1 || !double.IsFinite(MinimumSuccessRate6h))
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumSuccessRate6h));
        }

        if (MaximumStatusAge <= TimeSpan.Zero || MinimumDwellTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumStatusAge));
        }

        if (PricePremiumPercent < 0 || !double.IsFinite(PricePremiumPercent) ||
            MinimumPriceImprovementPercent < 0 || !double.IsFinite(MinimumPriceImprovementPercent) ||
            MinimumLatencyImprovementPercent < 0 || !double.IsFinite(MinimumLatencyImprovementPercent))
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumPricePremiumPercent));
        }

        if (RequiredConfirmations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(RequiredConfirmations));
        }
    }
}

public sealed record RouteEvaluation(
    RouteCandidate? Recommended,
    IReadOnlyList<RouteCandidate> EligibleCandidates,
    IReadOnlyList<RouteCandidate> PriceWindowCandidates,
    double? MinimumMultiplier,
    double MaximumAcceptedMultiplier);

public enum RouteDecisionReason
{
    NoCandidate,
    InitialRoute,
    CurrentRouteInvalid,
    AlreadyOptimal,
    BetterPrice,
    FasterWithinBudget,
    AwaitingConfirmation,
    MinimumDwellTime,
    InsufficientImprovement
}

public sealed record RouteDecision(
    RouteCandidate? Current,
    RouteCandidate? Target,
    bool ShouldSwitch,
    RouteDecisionReason Reason,
    double PricePremiumPercent,
    double? LatencyImprovementPercent,
    int ConfirmationCount,
    DateTimeOffset EvaluatedAt);

public sealed record RouteState
{
    public long? CurrentGroupId { get; init; }
    public long? PendingGroupId { get; init; }
    public int PendingConfirmationCount { get; init; }
    public DateTimeOffset? LastSwitchAt { get; init; }
}
