namespace AIHubRouter.Core;

public static class GroupUsageEstimator
{
    public const int DefaultSampleLimit = 100;

    public const double MinimumConfidence = 0.20;
    private static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromMinutes(1);

    public static IReadOnlyList<ProviderStatus> Estimate(
        IEnumerable<GroupUsageStatsPage> pages,
        DateTimeOffset now,
        TimeSpan maximumAge)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (maximumAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAge));
        }

        var observations = pages
            .SelectMany(page => page.Items)
            .Where(item => item.GroupId > 0)
            .GroupBy(item => item.GroupId);

        return observations
            .Select(group => EstimateGroup(group, now, maximumAge))
            .OrderBy(provider => provider.GroupId)
            .ToArray();
    }

    private static ProviderStatus EstimateGroup(
        IEnumerable<GroupUsageStat> group,
        DateTimeOffset now,
        TimeSpan maximumAge)
    {
        var all = group.ToArray();
        var metadata = all
            .OrderByDescending(item => item.LastSampleAt)
            .First()
            ;
        var rawSamples = all
            .SelectMany(item => item.Samples)
            .Where(sample => sample.Timestamp is not null)
            .Where(sample => sample.FirstTokenLatencyMs is > 0)
            .Where(sample => double.IsFinite(sample.FirstTokenLatencyMs!.Value))
            .ToArray();

        var usable = rawSamples.Length > 0
            ? EstimateRawSamples(rawSamples, now, maximumAge)
            : EstimateAggregates(all, now, maximumAge);

        if (usable.AverageTtftMs is null)
        {
            return CreateProvider(metadata, null, 0, 0, null);
        }

        return CreateProvider(
            metadata,
            usable.AverageTtftMs,
            usable.SampleCount,
            usable.Confidence,
            usable.LatestSampleAt);
    }

    private static EstimateResult EstimateRawSamples(
        IReadOnlyList<GroupUsageSample> samples,
        DateTimeOffset now,
        TimeSpan maximumAge)
    {
        var observations = samples
            .Select(sample =>
            {
                var age = now - sample.Timestamp!.Value;
                var weight = ContinuousFreshness(age, maximumAge);
                return new WeightedSample(sample.FirstTokenLatencyMs!.Value, weight, sample.Timestamp.Value);
            })
            .Where(sample => sample.Weight > 0)
            .ToArray();
        if (observations.Length == 0)
        {
            return EstimateResult.Empty;
        }

        return BuildEstimate(observations, observations.Length, null);
    }

    private static EstimateResult EstimateAggregates(
        IReadOnlyList<GroupUsageStat> items,
        DateTimeOffset now,
        TimeSpan maximumAge)
    {
        var observations = items
            .Where(item => item.SampleCount > 0)
            .Where(item => item.AverageTtftMs is > 0)
            .Where(item => double.IsFinite(item.AverageTtftMs!.Value))
            .Where(item => item.LastSampleAt is not null)
            .Select(item =>
            {
                var age = now - item.LastSampleAt!.Value;
                var weight = ContinuousFreshness(age, maximumAge);
                return new WeightedSample(item.AverageTtftMs!.Value, weight, item.LastSampleAt.Value);
            })
            .Where(sample => sample.Weight > 0)
            .ToArray();
        if (observations.Length == 0)
        {
            return EstimateResult.Empty;
        }

        var sampleCount = items.Max(item => item.SampleCount);
        return BuildEstimate(observations, sampleCount, sampleCount);
    }

    private static EstimateResult BuildEstimate(
        IReadOnlyList<WeightedSample> observations,
        int sampleCount,
        double? effectiveSampleCountOverride)
    {
        var totalWeight = observations.Sum(sample => sample.Weight);
        var weightedMean = observations.Sum(sample => sample.Latency * sample.Weight) / totalWeight;
        var weightedVariance = observations.Sum(sample =>
        {
            var difference = sample.Latency - weightedMean;
            return sample.Weight * difference * difference;
        }) / totalWeight;
        var coefficientOfVariation = weightedMean > 0
            ? Math.Sqrt(weightedVariance) / weightedMean
            : double.PositiveInfinity;
        var freshness = Math.Clamp(
            observations.Average(sample => sample.Weight),
            0,
            1);
        var effectiveSampleCount = effectiveSampleCountOverride ??
            totalWeight * totalWeight / observations.Sum(sample => sample.Weight * sample.Weight);
        effectiveSampleCount *= freshness;
        var volume = 1 - Math.Exp(-effectiveSampleCount / 20d);
        var stability = 1 / (1 + coefficientOfVariation);
        var confidence = Math.Clamp(freshness * volume * stability, 0, 1);
        return new EstimateResult(
            weightedMean,
            sampleCount,
            confidence,
            observations.Max(sample => sample.Timestamp));
    }

    private static double ContinuousFreshness(TimeSpan age, TimeSpan maximumAge)
    {
        if (age < -MaximumFutureSkew || age > maximumAge)
        {
            return 0;
        }

        var halfLife = maximumAge.TotalMilliseconds / 2;
        return Math.Clamp(
            Math.Exp(-Math.Log(2) * Math.Max(age.TotalMilliseconds, 0) / halfLife),
            0,
            1);
    }

    private static ProviderStatus CreateProvider(
        GroupUsageStat item,
        double? weightedMean,
        int sampleCount,
        double confidence,
        DateTimeOffset? latestSampleAt)
    {
        return new ProviderStatus
        {
            Id = $"usage-group-{item.GroupId}",
            GroupId = item.GroupId,
            PlanType = item.Code,
            Platform = item.Platform,
            PriceMultiplier = item.RateMultiplier,
            Available = weightedMean is > 0 && confidence >= MinimumConfidence,
            Enabled = true,
            CheckedAt = latestSampleAt,
            FirstTokenLatencyMs = weightedMean,
            UsageSampleCount = sampleCount,
            LatencyConfidence = confidence
        };
    }

    private sealed record WeightedSample(double Latency, double Weight, DateTimeOffset Timestamp);

    private sealed record EstimateResult(
        double? AverageTtftMs,
        int SampleCount,
        double Confidence,
        DateTimeOffset? LatestSampleAt)
    {
        public static EstimateResult Empty => new(null, 0, 0, null);
    }
}
