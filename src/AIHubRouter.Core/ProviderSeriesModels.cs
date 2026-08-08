namespace AIHubRouter.Core;

public sealed record ProviderSeriesPage(
    DateTimeOffset? GeneratedAt,
    string Range,
    IReadOnlyDictionary<long, ProviderSeriesMetrics> Groups)
{
    public DateTimeOffset? LatestDataAt => Groups.Count == 0
        ? null
        : Groups.Values.Max(metrics => metrics.LatestSampleAt);
}

public sealed record ProviderSeriesMetrics(
    long GroupId,
    double? ProbeSuccessRate,
    double? AverageProbeLatencyMs,
    double? AverageUserTtftMs,
    int ProbeSampleCount,
    int UserTtftSampleCount,
    DateTimeOffset? LatestSampleAt);

public sealed record ProviderSeriesLoadStatus(
    bool Available,
    bool FromCache,
    bool IsDegraded,
    string Message)
{
    public static ProviderSeriesLoadStatus Live { get; } =
        new(true, false, false, "已使用实时供应商序列。");

    public static ProviderSeriesLoadStatus Cached(
        string message = "已使用供应商序列缓存。",
        bool isDegraded = false) =>
        new(true, true, isDegraded, message);

    public static ProviderSeriesLoadStatus Unavailable(string message) =>
        new(false, false, true, message);

    public static ProviderSeriesLoadStatus Disabled { get; } =
        new(false, false, false, "供应商序列评分已禁用。");
}
