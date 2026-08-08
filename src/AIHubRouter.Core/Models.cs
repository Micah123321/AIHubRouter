using System.Text.Json.Serialization;

namespace AIHubRouter.Core;

public sealed class GroupUsageStatsPage
{
    [JsonPropertyName("items")]
    public List<GroupUsageStat> Items { get; init; } = [];

    [JsonPropertyName("total")]
    public int Total { get; init; }

    [JsonPropertyName("sample_limit")]
    public int SampleLimit { get; init; }
}

public sealed class GroupUsageStat
{
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("platform")]
    public string Platform { get; init; } = string.Empty;

    [JsonPropertyName("rate_multiplier")]
    public double RateMultiplier { get; init; }

    [JsonPropertyName("avg_ttft_ms")]
    public double? AverageTtftMs { get; init; }

    [JsonPropertyName("sample_count")]
    public int SampleCount { get; init; }

    [JsonPropertyName("last_sample_at")]
    public DateTimeOffset? LastSampleAt { get; init; }

    [JsonPropertyName("group_id")]
    public long GroupId { get; init; }

    // Newer API versions may expose the raw observations alongside aggregates.
    [JsonPropertyName("samples")]
    public List<GroupUsageSample> Samples { get; init; } = [];
}

public sealed class GroupUsageSample
{
    public DateTimeOffset? Timestamp { get; init; }

    public double? FirstTokenLatencyMs { get; init; }
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
    [JsonConverter(typeof(NullAsFalseBooleanJsonConverter))]
    public bool Available { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("checkedAt")]
    public DateTimeOffset? CheckedAt { get; init; }

    [JsonPropertyName("firstTokenLatencyMs")]
    public double? FirstTokenLatencyMs { get; init; }

    [JsonPropertyName("cacheHitRate")]
    public double? CacheHitRate { get; set; }

    public int UsageSampleCount { get; init; }

    public double? LatencyConfidence { get; init; }

    [JsonPropertyName("outputTokensPerSecond")]
    public double? OutputTokensPerSecond { get; init; }

    [JsonPropertyName("successRates")]
    public Dictionary<string, double?> SuccessRates { get; init; } = [];

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }

    [JsonPropertyName("warningReasons")]
    public List<ProviderWarningReason> WarningReasons { get; init; } = [];

    public double? SuccessRate6h => SuccessRates.TryGetValue("6h", out var value) ? value : null;

    public bool HasWarnings => WarningReasons.Count > 0;
}

public sealed class ProviderWarningReason
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("count")]
    public int? Count { get; init; }
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
    TimeSpan MaximumStatusAge,
    IReadOnlyCollection<long>? BlacklistedGroupIds = null,
    double MinimumConfidence = BalancedRoutingPolicy.DefaultMinimumConfidence);

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
    public const double DefaultMinimumScoreAdvantageToSwitch = 0.10;
    public const double DefaultMinimumPriceMultiplier = 0;
    public const double DefaultMaximumPriceMultiplier = 0.15;
    public const double DefaultConfidenceImpact = 1;
    public const double DefaultMinimumConfidence = 0.90;
    public const double DefaultProviderSeriesWeight = 0.20;

    public string Platform { get; init; } = "openai";
    public RoutingMode Mode { get; init; } = RoutingMode.Balanced;
    public TimeSpan MaximumStatusAge { get; init; } = TimeSpan.FromMinutes(15);
    public double? PriceWeightOverride { get; init; }
    public double? MinimumScoreAdvantageOverride { get; init; }
    public double MinimumPriceMultiplier { get; init; } = DefaultMinimumPriceMultiplier;
    public double MaximumPriceMultiplier { get; init; } = DefaultMaximumPriceMultiplier;
    public double ConfidenceImpact { get; init; } = DefaultConfidenceImpact;
    public double MinimumConfidence { get; init; } = DefaultMinimumConfidence;
    public double ProviderSeriesWeight { get; init; } = DefaultProviderSeriesWeight;
    public IReadOnlyCollection<long> BlacklistedGroupIds { get; init; } = [];

    public double PriceWeight => PriceWeightOverride ?? Mode switch
    {
        RoutingMode.Economy => 0.90,
        RoutingMode.Balanced => 0.50,
        RoutingMode.Speed => 0.10,
        _ => 0.50
    };

    public double LatencyWeight => 1 - PriceWeight;
    public double MinimumScoreAdvantageToSwitch => MinimumScoreAdvantageOverride ??
        DefaultMinimumScoreAdvantageToSwitch;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Platform))
        {
            throw new ArgumentException("路由平台不能为空。", nameof(Platform));
        }

        if (MaximumStatusAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumStatusAge));
        }

        if (PriceWeight is <= 0 or >= 1 || !double.IsFinite(PriceWeight))
        {
            throw new ArgumentOutOfRangeException(nameof(PriceWeightOverride));
        }

        if (MinimumScoreAdvantageOverride is { } minimumScoreAdvantage &&
            (minimumScoreAdvantage < 0 || !double.IsFinite(minimumScoreAdvantage)))
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumScoreAdvantageOverride));
        }

        if (MinimumPriceMultiplier < 0 ||
            !double.IsFinite(MinimumPriceMultiplier) ||
            !double.IsFinite(MaximumPriceMultiplier) ||
            MaximumPriceMultiplier < MinimumPriceMultiplier)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumPriceMultiplier));
        }

        if (ConfidenceImpact is < 0 or > 2 || !double.IsFinite(ConfidenceImpact))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ConfidenceImpact),
                "置信度影响强度必须在 0 到 2 之间。");
        }

        if (MinimumConfidence is < 0 or > 1 || !double.IsFinite(MinimumConfidence))
        {
            throw new ArgumentOutOfRangeException(
                nameof(MinimumConfidence),
                "最低置信度必须在 0 到 1 之间。");
        }

        if (ProviderSeriesWeight is < 0 or > 1 || !double.IsFinite(ProviderSeriesWeight))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ProviderSeriesWeight),
                "供应商序列权重必须在 0 到 1 之间。");
        }
    }
}

public sealed record RouteEvaluation(
    RouteCandidate? Recommended,
    RouteCandidate? Baseline,
    IReadOnlyList<RouteCandidate> EligibleCandidates,
    IReadOnlyList<RouteCandidate> TradeoffCandidates,
    double? MinimumMultiplier,
    double PriceWeight,
    double LatencyWeight,
    double ConfidenceImpact,
    double MinimumConfidence,
    IReadOnlyDictionary<long, double> ProviderSeriesScores,
    double ProviderSeriesWeight);

public enum RouteDecisionReason
{
    NoCandidate,
    InitialRoute,
    CurrentRouteInvalid,
    AlreadyOptimal,
    ScoreAdvantageTooSmall,
    BetterPrice,
    FasterForWeightedTradeoff
}

public sealed record RouteDecision(
    RouteCandidate? Current,
    RouteCandidate? Target,
    bool ShouldSwitch,
    RouteDecisionReason Reason,
    double PricePremiumPercent,
    double? LatencyImprovementPercent,
    DateTimeOffset EvaluatedAt);

public sealed record RouteState
{
    public long? CurrentGroupId { get; init; }
}
