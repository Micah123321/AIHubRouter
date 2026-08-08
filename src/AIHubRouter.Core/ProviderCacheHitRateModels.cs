namespace AIHubRouter.Core;

public sealed record ProviderCacheHitRatePage(
    DateTimeOffset? GeneratedAt,
    IReadOnlyDictionary<long, double> Groups);

public sealed record ProviderCacheHitRateLoadStatus(
    bool Available,
    bool FromCache,
    bool IsDegraded,
    string Message)
{
    public static ProviderCacheHitRateLoadStatus Live { get; } =
        new(true, false, false, "已使用供应商缓存命中率。");

    public static ProviderCacheHitRateLoadStatus Cached(
        string message = "已使用供应商缓存命中率缓存。",
        bool isDegraded = false) =>
        new(true, true, isDegraded, message);

    public static ProviderCacheHitRateLoadStatus Unavailable(string message) =>
        new(false, false, true, message);

    public static ProviderCacheHitRateLoadStatus Disabled { get; } =
        new(false, false, false, "供应商综合评分已禁用。");
}
