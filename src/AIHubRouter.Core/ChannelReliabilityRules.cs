namespace AIHubRouter.Core;

public static class ChannelReliabilityRules
{
    public static DetectorModelCapabilityStatus ParseCapabilityStatus(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "healthy" => DetectorModelCapabilityStatus.Healthy,
            "failed" => DetectorModelCapabilityStatus.Failed,
            _ => DetectorModelCapabilityStatus.Unknown
        };

    public static IReadOnlyList<string> SelectProbeModels(
        IReadOnlyDictionary<string, string>? modelHealth,
        DetectorBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!binding.Enabled || modelHealth is null)
        {
            return [];
        }

        var declaredModels = (binding.Models ?? [])
            .Select(DetectorModelNames.Normalize)
            .Where(model => model is not null)
            .Select(model => model!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return DetectorModelNames.Models
            .Where(model => IsHealthy(modelHealth, model) &&
                (declaredModels.Count == 0 || declaredModels.Contains(model)))
            .ToArray();
    }

    public static bool IsHardVerdict(DetectorVerdict verdict) =>
        verdict is DetectorVerdict.PossibleNonGpt or
            DetectorVerdict.JuiceMixed or
            DetectorVerdict.ProbabilityOnlyMixed;

    public static ChannelReliabilityStatus ResolveStatus(
        IReadOnlyCollection<DetectorResult>? results)
    {
        if (results is null || results.Count == 0)
        {
            return ChannelReliabilityStatus.EvidenceInsufficient;
        }

        if (results.All(result => result.Status == ChannelReliabilityStatus.Unconfigured))
        {
            return ChannelReliabilityStatus.Unconfigured;
        }

        if (results.Any(result => result.Status == ChannelReliabilityStatus.Unavailable))
        {
            return ChannelReliabilityStatus.Unavailable;
        }

        if (results.Any(result => result.IsQuarantineEligible))
        {
            return ChannelReliabilityStatus.Quarantined;
        }

        if (results.All(result =>
                result.Status == ChannelReliabilityStatus.Passed &&
                result.Verdict == DetectorVerdict.Passed))
        {
            return ChannelReliabilityStatus.Passed;
        }

        return ChannelReliabilityStatus.EvidenceInsufficient;
    }

    public static ChannelReliabilityStatus ResolveKeyStatus(
        IReadOnlyCollection<ChannelReliabilityResult>? results)
    {
        if (results is null || results.Count == 0)
        {
            return ChannelReliabilityStatus.Unconfigured;
        }

        if (results.Any(result => result.Status == ChannelReliabilityStatus.Quarantined))
        {
            return ChannelReliabilityStatus.Quarantined;
        }

        if (results.All(result => result.Status == ChannelReliabilityStatus.Unconfigured))
        {
            return ChannelReliabilityStatus.Unconfigured;
        }

        if (results.Any(result => result.Status == ChannelReliabilityStatus.Unavailable))
        {
            return ChannelReliabilityStatus.Unavailable;
        }

        if (results.All(result => result.Status == ChannelReliabilityStatus.Passed))
        {
            return ChannelReliabilityStatus.Passed;
        }

        return ChannelReliabilityStatus.EvidenceInsufficient;
    }

    private static bool IsHealthy(
        IReadOnlyDictionary<string, string> modelHealth,
        string model)
    {
        if (modelHealth.TryGetValue(model, out var status))
        {
            return ParseCapabilityStatus(status) == DetectorModelCapabilityStatus.Healthy;
        }

        return modelHealth.Any(entry =>
            string.Equals(entry.Key, model, StringComparison.OrdinalIgnoreCase) &&
            ParseCapabilityStatus(entry.Value) == DetectorModelCapabilityStatus.Healthy);
    }
}
