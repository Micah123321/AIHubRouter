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

        var groups = availableGroups
            .Where(group => group.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            .Where(group => group.Platform.Equals(criteria.Platform, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(group => group.Id);

        return providers
            .Where(provider => provider.Enabled && provider.Available)
            .Where(provider => provider.GroupId is > 0 && groups.ContainsKey(provider.GroupId.Value))
            .Where(provider => provider.Platform.Equals(criteria.Platform, StringComparison.OrdinalIgnoreCase))
            .Where(provider => provider.PriceMultiplier >= 0 && double.IsFinite(provider.PriceMultiplier))
            .Where(provider => IsFresh(provider.CheckedAt, now, criteria.MaximumStatusAge))
            .Where(provider => (provider.SuccessRate6h ?? 0) >= criteria.MinimumSuccessRate6h)
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
                .ThenByDescending(candidate => candidate.Provider.SuccessRate6h ?? 0)
                .ThenBy(candidate => candidate.Provider.FirstTokenLatencyMs ?? double.MaxValue)
                .First())
            .OrderBy(candidate => candidate.EffectiveMultiplier)
            .ThenByDescending(candidate => candidate.Provider.SuccessRate6h ?? 0)
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

        var groups = availableGroups
            .Where(group => group.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            .Where(group => group.Platform.Equals(policy.Platform, StringComparison.OrdinalIgnoreCase))
            .GroupBy(group => group.Id)
            .ToDictionary(group => group.Key, group => group.First());

        var eligible = providers
            .Where(provider => provider.Enabled && provider.Available)
            .Where(provider => provider.GroupId is > 0 && groups.ContainsKey(provider.GroupId.Value))
            .Where(provider => provider.Platform.Equals(policy.Platform, StringComparison.OrdinalIgnoreCase))
            .Where(provider => provider.PriceMultiplier >= 0 && double.IsFinite(provider.PriceMultiplier))
            .Where(provider => IsFresh(provider.CheckedAt, now, policy.MaximumStatusAge))
            .Where(provider => (provider.SuccessRate6h ?? 0) >= policy.MinimumSuccessRate6h)
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
                .ThenByDescending(candidate => candidate.Provider.SuccessRate6h ?? 0)
                .ThenBy(candidate => candidate.EffectiveMultiplier)
                .First())
            .ToArray();

        if (eligible.Length == 0)
        {
            return new RouteEvaluation(null, eligible, eligible, null, double.NaN);
        }

        var minimumMultiplier = eligible.Min(candidate => candidate.EffectiveMultiplier);
        var maximumAcceptedMultiplier = minimumMultiplier == 0
            ? 0
            : minimumMultiplier * (1 + policy.PricePremiumPercent / 100);
        var priceWindow = eligible
            .Where(candidate => candidate.EffectiveMultiplier <= maximumAcceptedMultiplier + 1e-12)
            .OrderBy(candidate => NormalizeLatency(candidate.Provider.FirstTokenLatencyMs))
            .ThenByDescending(candidate => candidate.Provider.SuccessRate6h ?? 0)
            .ThenBy(candidate => candidate.EffectiveMultiplier)
            .ThenBy(candidate => candidate.Group.Id)
            .ToArray();

        return new RouteEvaluation(
            priceWindow.FirstOrDefault(),
            eligible,
            priceWindow,
            minimumMultiplier,
            maximumAcceptedMultiplier);
    }

    internal static double NormalizeLatency(double? latency)
    {
        return latency is >= 0 && double.IsFinite(latency.Value)
            ? latency.Value
            : double.MaxValue;
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
