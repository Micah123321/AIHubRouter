using System.Text.Json.Serialization;

namespace AIHubRouter.Core;

public static class DetectorModelNames
{
    public const string Sol = "sol";
    public const string Terra = "terra";
    public const string Luna = "luna";

    public static IReadOnlyList<string> Models { get; } = [Sol, Terra, Luna];

    public static bool IsSupported(string? model) =>
        Models.Any(candidate => string.Equals(candidate, model?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string? Normalize(string? model)
    {
        var trimmed = model?.Trim();
        return Models.FirstOrDefault(candidate =>
            string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase));
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DetectorModelCapabilityStatus
{
    Unknown,
    Healthy,
    Failed
}

public sealed record DetectorModelCapability
{
    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public DetectorModelCapabilityStatus Status { get; init; }
}

public sealed record DetectorBinding
{
    [JsonPropertyName("keyId")]
    public long KeyId { get; init; }

    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; init; } = string.Empty;

    [JsonPropertyName("models")]
    public string[] Models { get; init; } = [];

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChannelReliabilityStatus
{
    Unconfigured,
    Unavailable,
    EvidenceInsufficient,
    Passed,
    Quarantined
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DetectorVerdict
{
    EvidenceInsufficient,
    Passed,
    PossibleNonGpt,
    JuiceMixed,
    ProbabilityOnlyMixed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DetectorErrorCategory
{
    None,
    MissingBinding,
    MissingCredential,
    WorkerUnavailable,
    WorkerStartFailed,
    Timeout,
    Cancelled,
    HttpError,
    StreamTruncated,
    InvalidResponse,
    EvidenceInsufficient,
    Unknown
}

public sealed record DetectorResult
{
    [JsonPropertyName("keyId")]
    public long KeyId { get; init; }

    [JsonPropertyName("groupId")]
    public long? GroupId { get; init; }

    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public ChannelReliabilityStatus Status { get; init; } = ChannelReliabilityStatus.EvidenceInsufficient;

    [JsonPropertyName("verdict")]
    public DetectorVerdict Verdict { get; init; } = DetectorVerdict.EvidenceInsufficient;

    [JsonPropertyName("errorCategory")]
    public DetectorErrorCategory ErrorCategory { get; init; } = DetectorErrorCategory.None;

    [JsonPropertyName("checkedAt")]
    public DateTimeOffset? CheckedAt { get; init; }

    [JsonPropertyName("official")]
    public bool? Official { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("networkSummary")]
    public string? NetworkSummary { get; init; }

    [JsonPropertyName("evidenceSummary")]
    public string? EvidenceSummary { get; init; }

    [JsonIgnore]
    public bool IsHardAnomaly => ChannelReliabilityRules.IsHardVerdict(Verdict);

    [JsonIgnore]
    public bool IsQuarantineEligible =>
        IsHardAnomaly && ErrorCategory == DetectorErrorCategory.None;
}

public sealed record ChannelReliabilityResult
{
    [JsonPropertyName("keyId")]
    public long KeyId { get; init; }

    [JsonPropertyName("groupId")]
    public long? GroupId { get; init; }

    [JsonPropertyName("status")]
    public ChannelReliabilityStatus Status { get; init; } = ChannelReliabilityStatus.EvidenceInsufficient;

    [JsonPropertyName("verdict")]
    public DetectorVerdict? Verdict { get; init; }

    [JsonPropertyName("probedModels")]
    public IReadOnlyList<string> ProbedModels { get; init; } = [];

    [JsonPropertyName("modelResults")]
    public IReadOnlyList<DetectorResult> ModelResults { get; init; } = [];

    [JsonPropertyName("checkedAt")]
    public DateTimeOffset? CheckedAt { get; init; }

    [JsonPropertyName("groupChanged")]
    public bool GroupChanged { get; init; }

    [JsonPropertyName("quarantine")]
    public ChannelQuarantineRecord? Quarantine { get; init; }
}

public sealed record ChannelQuarantineRecord
{
    [JsonPropertyName("groupId")]
    public long GroupId { get; init; }

    [JsonPropertyName("quarantinedAt")]
    public DateTimeOffset QuarantinedAt { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; init; }

    [JsonPropertyName("verdict")]
    public DetectorVerdict Verdict { get; init; }

    [JsonPropertyName("sourceKeyId")]
    public long SourceKeyId { get; init; }

    [JsonPropertyName("sourceModel")]
    public string SourceModel { get; init; } = string.Empty;

    public bool IsActiveAt(DateTimeOffset now) => ExpiresAt > now;
}

public sealed record ChannelQuarantineSnapshot
{
    [JsonPropertyName("capturedAt")]
    public DateTimeOffset CapturedAt { get; init; }

    [JsonPropertyName("records")]
    public IReadOnlyList<ChannelQuarantineRecord> Records { get; init; } = [];

    [JsonIgnore]
    public IReadOnlyList<long> ActiveGroupIds => GetActiveGroupIds(CapturedAt);

    public bool IsActive(long groupId, DateTimeOffset now) =>
        Records.Any(record => record.GroupId == groupId && record.IsActiveAt(now));

    public IReadOnlyList<long> GetActiveGroupIds(DateTimeOffset now)
    {
        return Records
            .Where(record => record.IsActiveAt(now))
            .Select(record => record.GroupId)
            .Distinct()
            .Order()
            .ToArray();
    }
}

public sealed record ChannelReliabilityKeySummary
{
    [JsonPropertyName("keyId")]
    public long KeyId { get; init; }

    [JsonPropertyName("keyName")]
    public string KeyName { get; init; } = string.Empty;

    [JsonPropertyName("groupId")]
    public long? GroupId { get; init; }

    [JsonPropertyName("hasDetectorBinding")]
    public bool HasDetectorBinding { get; init; }

    [JsonPropertyName("hasDetectorCredential")]
    public bool HasDetectorCredential { get; init; }

    [JsonPropertyName("status")]
    public ChannelReliabilityStatus Status { get; init; } = ChannelReliabilityStatus.Unconfigured;

    [JsonPropertyName("verdict")]
    public DetectorVerdict? Verdict { get; init; }

    [JsonPropertyName("models")]
    public IReadOnlyList<string> Models { get; init; } = [];

    [JsonPropertyName("lastCheckedAt")]
    public DateTimeOffset? LastCheckedAt { get; init; }

    [JsonPropertyName("nextCheckAt")]
    public DateTimeOffset? NextCheckAt { get; init; }

    [JsonPropertyName("quarantinedUntil")]
    public DateTimeOffset? QuarantinedUntil { get; init; }
}

public sealed record ChannelReliabilityGroupSummary
{
    [JsonPropertyName("groupId")]
    public long GroupId { get; init; }

    [JsonPropertyName("groupName")]
    public string GroupName { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public ChannelReliabilityStatus Status { get; init; } = ChannelReliabilityStatus.EvidenceInsufficient;

    [JsonPropertyName("models")]
    public IReadOnlyList<string> Models { get; init; } = [];

    [JsonPropertyName("verdict")]
    public DetectorVerdict? Verdict { get; init; }

    [JsonPropertyName("sourceKeyId")]
    public long? SourceKeyId { get; init; }

    [JsonPropertyName("quarantinedUntil")]
    public DateTimeOffset? QuarantinedUntil { get; init; }
}

public sealed record ChannelReliabilityCycleResult
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset? StartedAt { get; init; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset? CompletedAt { get; init; }

    [JsonPropertyName("results")]
    public IReadOnlyList<ChannelReliabilityResult> Results { get; init; } = [];

    [JsonPropertyName("keys")]
    public IReadOnlyList<ChannelReliabilityKeySummary> Keys { get; init; } = [];

    [JsonPropertyName("groups")]
    public IReadOnlyList<ChannelReliabilityGroupSummary> Groups { get; init; } = [];

    [JsonPropertyName("quarantine")]
    public ChannelQuarantineSnapshot Quarantine { get; init; } = new();

    [JsonIgnore]
    public IReadOnlyList<long> ExcludedGroupIds => Quarantine.ActiveGroupIds;
}

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

        // The supported model set is intentionally fixed to the three provider models.
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

        if (results.Any(result => result.IsQuarantineEligible))
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

        if (results.All(result =>
                result.Status == ChannelReliabilityStatus.Passed &&
                result.Verdict == DetectorVerdict.Passed))
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
