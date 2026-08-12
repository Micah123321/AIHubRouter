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

    public static IReadOnlyList<string> SelectProbeModels(DetectorBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!binding.Enabled)
        {
            return [];
        }

        var declaredModels = (binding.Models ?? [])
            .Select(DetectorModelNames.Normalize)
            .Where(model => model is not null)
            .Select(model => model!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return DetectorModelNames.Models
            .Where(model => declaredModels.Count == 0 || declaredModels.Contains(model))
            .ToArray();
    }

    public static IReadOnlyList<DetectorResult> SelectSummaryResults(
        IReadOnlyCollection<DetectorResult> results,
        DetectorBinding binding)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(binding);
        var models = SelectProbeModels(binding);
        var primaryModel = models.Contains(DetectorModelNames.Sol, StringComparer.OrdinalIgnoreCase)
            ? DetectorModelNames.Sol
            : models.Contains(DetectorModelNames.Luna, StringComparer.OrdinalIgnoreCase)
                ? DetectorModelNames.Luna
                : models.FirstOrDefault();
        if (primaryModel is null)
        {
            return results.ToArray();
        }

        var primaryResults = results
            .Where(result => string.Equals(result.Model, primaryModel, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return primaryResults.Length > 0 ? primaryResults : results.ToArray();
    }

    public static bool IsHardVerdict(DetectorVerdict verdict) =>
        verdict is DetectorVerdict.PossibleNonGpt or
            DetectorVerdict.JuiceMixed or
            DetectorVerdict.ProbabilityOnlyMixed;

    public static bool IsHardOutcome(DetectorOutcomeCode outcomeCode) => outcomeCode is
        DetectorOutcomeCode.PossibleNonGpt or
        DetectorOutcomeCode.JuiceMismatchFingerprintStrong or
        DetectorOutcomeCode.JuiceMismatchFingerprintUnclear;

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

}
