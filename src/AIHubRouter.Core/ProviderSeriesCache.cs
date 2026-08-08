namespace AIHubRouter.Core;

internal sealed class ProviderSeriesCache(PersistentAppSettings settings)
{
    private ProviderSeriesPage? _page;
    private string? _cacheKey;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public async Task<(ProviderSeriesPage? Page, ProviderSeriesLoadStatus Status)> LoadAsync(
        IAIHubApiClient client,
        BalancedRoutingPolicy policy,
        DateTimeOffset now,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (policy.ProviderSeriesWeight <= 0)
        {
            return (null, ProviderSeriesLoadStatus.Disabled);
        }

        var range = settings.ProviderSeriesRange?.Trim() ?? string.Empty;
        var timezone = settings.ProviderSeriesTimezone?.Trim() ?? string.Empty;
        if (range.Length == 0 || timezone.Length == 0)
        {
            return (
                null,
                ProviderSeriesLoadStatus.Unavailable(
                    "供应商序列配置无效，已沿用基础评分。"));
        }

        var cachedPage = _page;
        var cacheKey = $"{range}\n{timezone}";
        var hasFreshCache =
            cachedPage is not null &&
            string.Equals(_cacheKey, cacheKey, StringComparison.Ordinal) &&
            now < _expiresAt &&
            IsFresh(cachedPage, now, policy.MaximumStatusAge);
        if (!forceRefresh && hasFreshCache)
        {
            return (cachedPage, ProviderSeriesLoadStatus.Cached());
        }

        try
        {
            var page = await client.GetProviderSeriesAsync(
                range,
                timezone,
                cancellationToken);
            if (!IsFresh(page, now, policy.MaximumStatusAge))
            {
                throw new InvalidDataException("供应商序列数据已过期。");
            }

            _page = page;
            _cacheKey = cacheKey;
            _expiresAt = now.AddSeconds(
                Math.Clamp(settings.ProviderSeriesCacheSeconds, 30, 3600));
            return (page, ProviderSeriesLoadStatus.Live);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is AIHubApiException or
                HttpRequestException or
                TaskCanceledException or
                InvalidDataException)
        {
            if (hasFreshCache)
            {
                return (
                    cachedPage,
                    ProviderSeriesLoadStatus.Cached(
                        "供应商序列刷新失败，已使用缓存。",
                        isDegraded: true));
            }

            return (
                null,
                ProviderSeriesLoadStatus.Unavailable(GetSafeErrorMessage(exception)));
        }
    }

    private static bool IsFresh(
        ProviderSeriesPage page,
        DateTimeOffset now,
        TimeSpan maximumAge)
    {
        if (page.LatestDataAt is not { } latest)
        {
            return false;
        }

        var age = now - latest;
        return age >= TimeSpan.FromMinutes(-1) && age <= maximumAge;
    }

    private static string GetSafeErrorMessage(Exception exception) =>
        exception switch
        {
            HttpRequestException => "供应商序列网络请求失败，已沿用基础评分。",
            TaskCanceledException => "供应商序列请求超时，已沿用基础评分。",
            AIHubApiException => "供应商序列接口返回错误，已沿用基础评分。",
            InvalidDataException => "供应商序列数据不可用，已沿用基础评分。",
            _ => "供应商序列加载失败，已沿用基础评分。"
        };
}
