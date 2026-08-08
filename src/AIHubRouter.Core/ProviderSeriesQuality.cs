namespace AIHubRouter.Core;

internal static class ProviderSeriesQuality
{
    // ha-min: 至少两次探测排除单次偶然值；样本量差异扩大时升级为连续置信度。
    private const int MinimumProbeSamples = 2;

    public static IReadOnlyDictionary<long, double> Calculate(
        IReadOnlyCollection<RouteCandidate> candidates,
        IReadOnlyDictionary<long, ProviderSeriesMetrics>? metrics,
        DateTimeOffset now,
        TimeSpan maximumAge)
    {
        if (metrics is null || metrics.Count == 0 || candidates.Count == 0)
        {
            return new Dictionary<long, double>();
        }

        var candidateMetrics = candidates
            .Select(candidate => candidate.Group.Id)
            .Distinct()
            .Where(metrics.ContainsKey)
            .Where(groupId => IsUsable(metrics[groupId], now, maximumAge))
            .ToDictionary(groupId => groupId, groupId => metrics[groupId]);
        // 缺少成功探测延迟的候选无法公平比较，不生成供应商质量分。
        var comparableMetrics = candidateMetrics
            .Where(pair => HasProbeLatency(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var probeLatencyRange = BuildRange(
            comparableMetrics.Values.Select(value => value.AverageProbeLatencyMs),
            comparableMetrics.Count);
        var userTtftRange = BuildRange(
            comparableMetrics.Values.Select(value =>
                value.UserTtftSampleCount > 0 ? value.AverageUserTtftMs : null),
            comparableMetrics.Count);
        var result = new Dictionary<long, double>();

        foreach (var (groupId, value) in comparableMetrics)
        {
            var components = new List<double>(3);
            components.Add(value.ProbeSuccessRate!.Value);

            AddInverseLatency(components, value.AverageProbeLatencyMs, probeLatencyRange);
            AddInverseLatency(components, value.AverageUserTtftMs, userTtftRange);
            if (components.Count > 0)
            {
                result[groupId] = components.Average();
            }
        }

        return result;
    }

    private static bool IsUsable(
        ProviderSeriesMetrics metrics,
        DateTimeOffset now,
        TimeSpan maximumAge) =>
        metrics.ProbeSampleCount >= MinimumProbeSamples &&
        metrics.ProbeSuccessRate is { } successRate &&
        successRate is >= 0 and <= 1 &&
        double.IsFinite(successRate) &&
        metrics.LatestSampleAt is { } latestSampleAt &&
        IsFresh(latestSampleAt, now, maximumAge);

    private static bool HasProbeLatency(ProviderSeriesMetrics metrics) =>
        metrics.AverageProbeLatencyMs is > 0 and var latency &&
        double.IsFinite(latency);

    private static bool IsFresh(
        DateTimeOffset latestSampleAt,
        DateTimeOffset now,
        TimeSpan maximumAge)
    {
        var age = now - latestSampleAt;
        return age >= TimeSpan.FromMinutes(-1) && age <= maximumAge;
    }

    private static (double Minimum, double Maximum)? BuildRange(
        IEnumerable<double?> values,
        int expectedCount)
    {
        var valid = values
            .Where(value => value is > 0 && double.IsFinite(value.Value))
            .Select(value => value!.Value)
            .ToArray();
        if (valid.Length < 2 || valid.Length != expectedCount)
        {
            return null;
        }

        var minimum = valid.Min();
        var maximum = valid.Max();
        return maximum - minimum > 1e-9 ? (minimum, maximum) : null;
    }

    private static void AddInverseLatency(
        ICollection<double> components,
        double? latency,
        (double Minimum, double Maximum)? range)
    {
        if (range is not { } bounds ||
            latency is not > 0 ||
            !double.IsFinite(latency.Value))
        {
            return;
        }

        components.Add(Math.Clamp(
            1 - (latency.Value - bounds.Minimum) / (bounds.Maximum - bounds.Minimum),
            0,
            1));
    }
}
