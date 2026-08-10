namespace AIHubRouter.Core;

public static class RoutingEngine
{
    public static RouteCandidate? SelectCheapest(
        IEnumerable<ProviderStatus> providers,
        IEnumerable<GroupInfo> availableGroups,
        IReadOnlyDictionary<long, double> userGroupRates,
        RoutingCriteria criteria,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(availableGroups);
        ArgumentNullException.ThrowIfNull(userGroupRates);
        ArgumentNullException.ThrowIfNull(criteria);

        var blacklistedGroupIds = criteria.BlacklistedGroupIds?.ToHashSet() ?? [];

        var groups = availableGroups
            .Where(group => group.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            .Where(group => group.Platform.Equals(criteria.Platform, StringComparison.OrdinalIgnoreCase))
            .Where(group => !blacklistedGroupIds.Contains(group.Id))
            .ToDictionary(group => group.Id);

        return providers
            .Where(provider => provider.Enabled && provider.Available)
            .Where(provider => HasSufficientConfidence(provider, criteria.MinimumConfidence))
            .Where(provider => provider.GroupId is > 0 && groups.ContainsKey(provider.GroupId.Value))
            .Where(provider => provider.Platform.Equals(criteria.Platform, StringComparison.OrdinalIgnoreCase))
            .Where(provider => provider.PriceMultiplier >= 0 && double.IsFinite(provider.PriceMultiplier))
            .Where(provider => IsFresh(provider.CheckedAt, now, criteria.MaximumStatusAge))
            .Select(provider =>
            {
                var group = groups[provider.GroupId!.Value];
                var hasOverride = userGroupRates.TryGetValue(group.Id, out var overrideRate);
                var effectiveRate = hasOverride ? overrideRate : provider.PriceMultiplier;
                return new RouteCandidate(provider, group, effectiveRate, hasOverride);
            })
            .Where(candidate => candidate.EffectiveMultiplier >= 0 && double.IsFinite(candidate.EffectiveMultiplier))
            .Where(candidate => candidate.EffectiveMultiplier <= BalancedRoutingPolicy.DefaultMaximumPriceMultiplier)
            .GroupBy(candidate => candidate.Group.Id)
            .Select(group => group
                .OrderBy(candidate => candidate.EffectiveMultiplier)
                .ThenBy(candidate => candidate.Provider.FirstTokenLatencyMs ?? double.MaxValue)
                .First())
            .OrderBy(candidate => candidate.EffectiveMultiplier)
            .ThenBy(candidate => candidate.Provider.FirstTokenLatencyMs ?? double.MaxValue)
            .ThenBy(candidate => candidate.Group.Id)
            .FirstOrDefault();
    }

    public static RouteEvaluation Evaluate(
        IEnumerable<ProviderStatus> providers,
        IEnumerable<GroupInfo> availableGroups,
        IReadOnlyDictionary<long, double> userGroupRates,
        BalancedRoutingPolicy policy,
        DateTimeOffset now,
        IReadOnlyDictionary<long, ProviderSeriesMetrics>? providerSeriesMetrics = null,
        IReadOnlySet<long>? excludedGroupIds = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(availableGroups);
        ArgumentNullException.ThrowIfNull(userGroupRates);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();

        var blacklistedGroupIds = policy.BlacklistedGroupIds.ToHashSet();
        var excluded = excludedGroupIds ?? new HashSet<long>();

        var groups = availableGroups
            .Where(group => group.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            .Where(group => group.Platform.Equals(policy.Platform, StringComparison.OrdinalIgnoreCase))
            .Where(group => !blacklistedGroupIds.Contains(group.Id))
            .Where(group => !excluded.Contains(group.Id))
            .GroupBy(group => group.Id)
            .ToDictionary(group => group.Key, group => group.First());

        var eligible = providers
            .Where(provider => provider.Enabled && provider.Available)
            .Where(provider => HasSufficientConfidence(provider, policy.MinimumConfidence))
            .Where(provider => provider.GroupId is > 0 && groups.ContainsKey(provider.GroupId.Value))
            .Where(provider => provider.Platform.Equals(policy.Platform, StringComparison.OrdinalIgnoreCase))
            .Where(provider => provider.PriceMultiplier >= 0 && double.IsFinite(provider.PriceMultiplier))
            .Where(provider => IsFresh(provider.CheckedAt, now, policy.MaximumStatusAge))
            .Select(provider =>
            {
                var group = groups[provider.GroupId!.Value];
                var hasOverride = userGroupRates.TryGetValue(group.Id, out var overrideRate);
                var effectiveRate = hasOverride ? overrideRate : provider.PriceMultiplier;
                return new RouteCandidate(provider, group, effectiveRate, hasOverride);
            })
            .Where(candidate => candidate.EffectiveMultiplier >= 0 && double.IsFinite(candidate.EffectiveMultiplier))
            .Where(candidate => IsWithinPriceRange(candidate.EffectiveMultiplier, policy))
            .GroupBy(candidate => candidate.Group.Id)
            .Select(group => group
                .OrderBy(candidate => NormalizeLatency(candidate.Provider.FirstTokenLatencyMs))
                .ThenBy(candidate => candidate.EffectiveMultiplier)
                .First())
            .ToArray();
        var providerSeriesScores = ProviderSeriesQuality.Calculate(
            eligible,
            providerSeriesMetrics,
            now,
            policy.MaximumStatusAge);

        if (eligible.Length == 0)
        {
            return new RouteEvaluation(
                null,
                null,
                eligible,
                eligible,
                null,
                policy.PriceWeight,
                policy.LatencyWeight,
                policy.ConfidenceImpact,
                policy.MinimumConfidence,
                providerSeriesScores,
                policy.ProviderSeriesWeight,
                policy.Mode);
        }

        var measured = eligible
            .Where(candidate => IsKnownLatency(candidate.Provider.FirstTokenLatencyMs))
            .ToArray();
        var decisionPool = measured.Length > 0 ? measured : eligible;
        var minimumMultiplier = decisionPool.Min(candidate => candidate.EffectiveMultiplier);
        var cheapest = decisionPool
            .Where(candidate => NearlyEqual(candidate.EffectiveMultiplier, minimumMultiplier))
            .OrderBy(candidate => NormalizeLatency(candidate.Provider.FirstTokenLatencyMs))
            .ThenBy(candidate => candidate.Group.Id)
            .ToArray();

        if (minimumMultiplier == 0 || measured.Length == 0)
        {
            return new RouteEvaluation(
                cheapest.FirstOrDefault(),
                cheapest.FirstOrDefault(),
                eligible,
                cheapest,
                minimumMultiplier,
                policy.PriceWeight,
                policy.LatencyWeight,
                policy.ConfidenceImpact,
                policy.MinimumConfidence,
                providerSeriesScores,
                policy.ProviderSeriesWeight,
                policy.Mode);
        }

        var baseline = cheapest[0];
        var baselineLatency = baseline.Provider.FirstTokenLatencyMs!.Value;
        if (baselineLatency <= 0)
        {
            return new RouteEvaluation(
                baseline,
                baseline,
                eligible,
                cheapest,
                minimumMultiplier,
                policy.PriceWeight,
                policy.LatencyWeight,
                policy.ConfidenceImpact,
                policy.MinimumConfidence,
                providerSeriesScores,
                policy.ProviderSeriesWeight,
                policy.Mode);
        }

        var tradeoff = decisionPool
            .Select(candidate => new
            {
                Candidate = candidate,
                Score = CalculateTradeoffScore(
                    minimumMultiplier,
                    baseline,
                    candidate,
                    policy.PriceWeight,
                    policy.LatencyWeight,
                    policy.ConfidenceImpact,
                    providerSeriesScores,
                    policy.ProviderSeriesWeight,
                    policy.Mode)
            })
            .Where(candidate =>
                candidate.Candidate.Group.Id == baseline.Group.Id ||
                candidate.Score > 1e-9)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Candidate.EffectiveMultiplier)
            .ThenBy(candidate => NormalizeLatency(candidate.Candidate.Provider.FirstTokenLatencyMs))
            .ThenBy(candidate => candidate.Candidate.Group.Id)
            .Select(candidate => candidate.Candidate)
            .ToArray();

        return new RouteEvaluation(
            tradeoff.FirstOrDefault() ?? baseline,
            baseline,
            eligible,
            tradeoff.Length > 0 ? tradeoff : cheapest,
            minimumMultiplier,
            policy.PriceWeight,
            policy.LatencyWeight,
            policy.ConfidenceImpact,
            policy.MinimumConfidence,
            providerSeriesScores,
            policy.ProviderSeriesWeight,
            policy.Mode);
    }

    internal static double NormalizeLatency(double? latency)
    {
        return latency is > 0 && double.IsFinite(latency.Value)
            ? latency.Value
            : double.MaxValue;
    }

    internal static double ApplyLatencyDiminishingReturns(double latency, RoutingMode mode)
    {
        if (mode != RoutingMode.Economy ||
            latency <= 0 ||
            !double.IsFinite(latency))
        {
            return latency;
        }

        var diminishingThreshold = BalancedRoutingPolicy.EconomyLatencyDiminishingThresholdMs;
        if (latency < diminishingThreshold)
        {
            return diminishingThreshold -
                (diminishingThreshold - latency) *
                BalancedRoutingPolicy.EconomyLatencyDiminishingFactor;
        }

        var severeThreshold = BalancedRoutingPolicy.EconomySevereLatencyThresholdMs;
        if (latency <= severeThreshold)
        {
            return latency;
        }

        return severeThreshold +
            (latency - severeThreshold) * BalancedRoutingPolicy.EconomySevereLatencyFactor;
    }

    private static bool HasSufficientConfidence(ProviderStatus provider, double minimumConfidence) =>
        provider.LatencyConfidence is { } confidence &&
        double.IsFinite(confidence) &&
        confidence >= minimumConfidence;

    public static double? CalculateWeightedScore(
        RouteEvaluation evaluation,
        RouteCandidate candidate)
    {
        var minimumMultiplier = evaluation.MinimumMultiplier;
        var baselineLatency = evaluation.Baseline?.Provider.FirstTokenLatencyMs;
        var candidateLatency = candidate.Provider.FirstTokenLatencyMs;
        if (minimumMultiplier is not > 0 ||
            baselineLatency is not > 0 ||
            candidateLatency is not > 0 ||
            !double.IsFinite(baselineLatency.Value) ||
            !double.IsFinite(candidateLatency.Value))
        {
            return null;
        }

        return CalculateTradeoffScore(
            minimumMultiplier.Value,
            evaluation.Baseline!,
            candidate,
            evaluation.PriceWeight,
            evaluation.LatencyWeight,
            evaluation.ConfidenceImpact,
            evaluation.ProviderSeriesScores,
            evaluation.ProviderSeriesWeight,
            evaluation.Mode);
    }

    private static bool IsKnownLatency(double? latency) =>
        latency is > 0 && double.IsFinite(latency.Value);

    public static bool IsWithinPriceRange(double multiplier, BalancedRoutingPolicy policy) =>
        multiplier >= policy.MinimumPriceMultiplier &&
        multiplier <= policy.MaximumPriceMultiplier;

    private static bool NearlyEqual(double left, double right) =>
        Math.Abs(left - right) <= 1e-12;

    private static double CalculateTradeoffScore(
        double minimumMultiplier,
        RouteCandidate baseline,
        RouteCandidate candidate,
        double priceWeight,
        double latencyWeight,
        double confidenceImpact,
        IReadOnlyDictionary<long, double> providerSeriesScores,
        double providerSeriesWeight,
        RoutingMode mode)
    {
        var pricePremiumRatio =
            (candidate.EffectiveMultiplier - minimumMultiplier) / minimumMultiplier;
        var conservativeBaselineLatency = GetConservativeLatency(
            baseline,
            baseline.Provider.FirstTokenLatencyMs!.Value,
            confidenceImpact);
        var conservativeCandidateLatency = GetConservativeLatency(
            candidate,
            candidate.Provider.FirstTokenLatencyMs!.Value,
            confidenceImpact);
        var effectiveBaselineLatency = ApplyLatencyDiminishingReturns(
            conservativeBaselineLatency,
            mode);
        var effectiveCandidateLatency = ApplyLatencyDiminishingReturns(
            conservativeCandidateLatency,
            mode);
        var speedupRatio = effectiveBaselineLatency / effectiveCandidateLatency - 1;
        var score = latencyWeight * speedupRatio - priceWeight * pricePremiumRatio;
        if (providerSeriesWeight <= 0 ||
            !providerSeriesScores.TryGetValue(baseline.Group.Id, out var baselineQuality) ||
            !providerSeriesScores.TryGetValue(candidate.Group.Id, out var candidateQuality))
        {
            return score;
        }

        var adjustedScore = score + providerSeriesWeight * (candidateQuality - baselineQuality);
        if (mode == RoutingMode.Economy &&
            effectiveCandidateLatency > BalancedRoutingPolicy.EconomySevereLatencyThresholdMs &&
            effectiveCandidateLatency > effectiveBaselineLatency &&
            score <= 0)
        {
            // Historical quality cannot offset a severe live-latency disadvantage.
            return Math.Min(adjustedScore, score);
        }

        return adjustedScore;
    }

    private static double GetConservativeLatency(
        RouteCandidate? candidate,
        double latency,
        double confidenceImpact)
    {
        var confidence = candidate?.Provider.LatencyConfidence;
        var effectiveConfidence = confidence is { } value && double.IsFinite(value)
            ? Math.Clamp(value, 0, 1)
            : 0;
        var uncertaintyPenalty = Math.Clamp(1 - effectiveConfidence, 0, 1);
        return latency * (1 + confidenceImpact * uncertaintyPenalty);
    }

    private static bool IsFresh(DateTimeOffset? checkedAt, DateTimeOffset now, TimeSpan maximumAge)
    {
        if (checkedAt is null)
        {
            return false;
        }

        var age = now - checkedAt.Value;
        return age >= TimeSpan.FromMinutes(-1) && age <= maximumAge;
    }
}
