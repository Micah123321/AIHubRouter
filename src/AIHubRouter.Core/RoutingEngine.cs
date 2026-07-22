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
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(availableGroups);
        ArgumentNullException.ThrowIfNull(userGroupRates);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();

        var blacklistedGroupIds = policy.BlacklistedGroupIds.ToHashSet();

        var groups = availableGroups
            .Where(group => group.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            .Where(group => group.Platform.Equals(policy.Platform, StringComparison.OrdinalIgnoreCase))
            .Where(group => !blacklistedGroupIds.Contains(group.Id))
            .GroupBy(group => group.Id)
            .ToDictionary(group => group.Key, group => group.First());

        var eligible = providers
            .Where(provider => provider.Enabled && provider.Available)
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
            .GroupBy(candidate => candidate.Group.Id)
            .Select(group => group
                .OrderBy(candidate => NormalizeLatency(candidate.Provider.FirstTokenLatencyMs))
                .ThenBy(candidate => candidate.EffectiveMultiplier)
                .First())
            .ToArray();

        if (eligible.Length == 0)
        {
            return new RouteEvaluation(
                null,
                null,
                eligible,
                eligible,
                null,
                policy.PriceWeight,
                policy.LatencyWeight);
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
                policy.LatencyWeight);
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
                policy.LatencyWeight);
        }

        var tradeoff = decisionPool
            .Select(candidate => new
            {
                Candidate = candidate,
                Score = CalculateTradeoffScore(
                    minimumMultiplier,
                    baselineLatency,
                    candidate,
                    policy.PriceWeight,
                    policy.LatencyWeight)
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
            policy.LatencyWeight);
    }

    internal static double NormalizeLatency(double? latency)
    {
        return latency is > 0 && double.IsFinite(latency.Value)
            ? latency.Value
            : double.MaxValue;
    }

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
            baselineLatency.Value,
            candidate,
            evaluation.PriceWeight,
            evaluation.LatencyWeight);
    }

    private static bool IsKnownLatency(double? latency) =>
        latency is > 0 && double.IsFinite(latency.Value);

    private static bool NearlyEqual(double left, double right) =>
        Math.Abs(left - right) <= 1e-12;

    private static double CalculateTradeoffScore(
        double minimumMultiplier,
        double baselineLatency,
        RouteCandidate candidate,
        double priceWeight,
        double latencyWeight)
    {
        var pricePremiumRatio =
            (candidate.EffectiveMultiplier - minimumMultiplier) / minimumMultiplier;
        var speedupRatio =
            baselineLatency / candidate.Provider.FirstTokenLatencyMs!.Value - 1;
        return latencyWeight * speedupRatio - priceWeight * pricePremiumRatio;
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
