using System.Text.Json.Serialization;

namespace AIHubRouter.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChannelReliabilityRunPhase
{
    Disabled,
    Idle,
    Queued,
    Running,
    Completed,
    CompletedWithWarnings,
    Failed,
    Cancelled
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChannelReliabilityTrigger
{
    Startup,
    Scheduled,
    Manual,
    Refresh,
    ConfigurationChanged,
    KeyGroupChanged,
    RoutingCycle
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChannelReliabilityProbeStage
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
    Skipped
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChannelReliabilityProbeFamily
{
    Process,
    Network,
    Juice,
    Identity,
    Coverage,
    Fingerprint,
    Verdict
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChannelReliabilityEventType
{
    RunQueued,
    RunStarted,
    ProbeQueued,
    ProbeStarted,
    ProbeCompleted,
    ProbeFailed,
    ProbeCancelled,
    ProbeSkipped,
    QuarantineApplied,
    QuarantineRejected,
    RunCompleted,
    RunFailed,
    RunCancelled
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChannelReliabilitySkipReason
{
    NotDue,
    MissingGroup,
    MissingBinding,
    MissingCredential,
    NoModels
}

public sealed record DetectorErrorCount
{
    [JsonPropertyName("category")]
    public string Category { get; init; } = string.Empty;

    [JsonPropertyName("count")]
    public int Count { get; init; }
}

public sealed record DetectorNetworkSummary
{
    [JsonPropertyName("logicalTasks")]
    public int LogicalTasks { get; init; }

    [JsonPropertyName("logicalCompleted")]
    public int LogicalCompleted { get; init; }

    [JsonPropertyName("successful")]
    public int Successful { get; init; }

    [JsonPropertyName("finalErrors")]
    public int FinalErrors { get; init; }

    [JsonPropertyName("cancelled")]
    public int Cancelled { get; init; }

    [JsonPropertyName("httpAttempts")]
    public int HttpAttempts { get; init; }

    [JsonPropertyName("retries")]
    public int Retries { get; init; }

    [JsonPropertyName("inFlight")]
    public int InFlight { get; init; }

    [JsonPropertyName("errorCategories")]
    public IReadOnlyList<DetectorErrorCount> ErrorCategories { get; init; } = [];
}

public sealed record DetectorEvidenceSummary
{
    [JsonPropertyName("reportSchemaVersion")]
    public int? ReportSchemaVersion { get; init; }

    [JsonPropertyName("outcomeCode")]
    public DetectorOutcomeCode OutcomeCode { get; init; }

    [JsonPropertyName("verdictAvailable")]
    public bool VerdictAvailable { get; init; }

    [JsonPropertyName("hardVerdict")]
    public bool HardVerdict { get; init; }

    [JsonPropertyName("juiceState")]
    public string JuiceState { get; init; } = "unknown";

    [JsonPropertyName("fingerprintState")]
    public string FingerprintState { get; init; } = "unknown";

    [JsonPropertyName("fingerprintModel")]
    public string? FingerprintModel { get; init; }

    [JsonPropertyName("juiceValidCompleted")]
    public int JuiceValidCompleted { get; init; }

    [JsonPropertyName("juiceCurrentSuccess")]
    public int JuiceCurrentSuccess { get; init; }

    [JsonPropertyName("juiceMixed")]
    public int JuiceMixed { get; init; }

    [JsonPropertyName("juiceNetworkErrors")]
    public int JuiceNetworkErrors { get; init; }

    [JsonPropertyName("outputRequests")]
    public int OutputRequests { get; init; }

    [JsonPropertyName("outputExact")]
    public int OutputExact { get; init; }

    [JsonPropertyName("coverageRequests")]
    public int CoverageRequests { get; init; }

    [JsonPropertyName("coverageHardAnomaly")]
    public bool CoverageHardAnomaly { get; init; }

    [JsonPropertyName("fingerprintEnabled")]
    public bool FingerprintEnabled { get; init; }

    [JsonPropertyName("fingerprintFormalEligible")]
    public bool? FingerprintFormalEligible { get; init; }

    [JsonPropertyName("evidenceInsufficient")]
    public bool EvidenceInsufficient { get; init; } = true;
}

public sealed record ChannelReliabilityProbeProgress
{
    [JsonPropertyName("keyId")]
    public long KeyId { get; init; }

    [JsonPropertyName("keyName")]
    public string KeyName { get; init; } = string.Empty;

    [JsonPropertyName("groupId")]
    public long? GroupId { get; init; }

    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("family")]
    public ChannelReliabilityProbeFamily Family { get; init; }

    [JsonPropertyName("stage")]
    public ChannelReliabilityProbeStage Stage { get; init; }

    [JsonPropertyName("queuedAt")]
    public DateTimeOffset? QueuedAt { get; init; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset? StartedAt { get; init; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset? CompletedAt { get; init; }

    [JsonPropertyName("durationMs")]
    public long? DurationMs { get; init; }

    [JsonPropertyName("status")]
    public ChannelReliabilityStatus? Status { get; init; }

    [JsonPropertyName("verdict")]
    public DetectorVerdict? Verdict { get; init; }

    [JsonPropertyName("outcomeCode")]
    public DetectorOutcomeCode? OutcomeCode { get; init; }

    [JsonPropertyName("errorCategory")]
    public DetectorErrorCategory ErrorCategory { get; init; }

    [JsonPropertyName("network")]
    public DetectorNetworkSummary? Network { get; init; }

    [JsonPropertyName("evidence")]
    public DetectorEvidenceSummary? Evidence { get; init; }

    [JsonPropertyName("quarantinedUntil")]
    public DateTimeOffset? QuarantinedUntil { get; init; }

    [JsonPropertyName("skipReason")]
    public ChannelReliabilitySkipReason? SkipReason { get; init; }

    [JsonPropertyName("capabilityStatus")]
    public DetectorModelCapabilityStatus? CapabilityStatus { get; init; }

    [JsonPropertyName("nextCheckAt")]
    public DateTimeOffset? NextCheckAt { get; init; }
}

public sealed record ChannelReliabilityAuditEvent
{
    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }

    [JsonPropertyName("runId")]
    public string RunId { get; init; } = string.Empty;

    [JsonPropertyName("occurredAt")]
    public DateTimeOffset OccurredAt { get; init; }

    [JsonPropertyName("eventType")]
    public ChannelReliabilityEventType EventType { get; init; }

    [JsonPropertyName("trigger")]
    public ChannelReliabilityTrigger Trigger { get; init; }

    [JsonPropertyName("keyId")]
    public long? KeyId { get; init; }

    [JsonPropertyName("keyName")]
    public string? KeyName { get; init; }

    [JsonPropertyName("groupId")]
    public long? GroupId { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("family")]
    public ChannelReliabilityProbeFamily? Family { get; init; }

    [JsonPropertyName("stage")]
    public ChannelReliabilityProbeStage? Stage { get; init; }

    [JsonPropertyName("durationMs")]
    public long? DurationMs { get; init; }

    [JsonPropertyName("status")]
    public ChannelReliabilityStatus? Status { get; init; }

    [JsonPropertyName("verdict")]
    public DetectorVerdict? Verdict { get; init; }

    [JsonPropertyName("outcomeCode")]
    public DetectorOutcomeCode? OutcomeCode { get; init; }

    [JsonPropertyName("errorCategory")]
    public DetectorErrorCategory ErrorCategory { get; init; }

    [JsonPropertyName("quarantinedUntil")]
    public DateTimeOffset? QuarantinedUntil { get; init; }

    [JsonPropertyName("skipReason")]
    public ChannelReliabilitySkipReason? SkipReason { get; init; }

    [JsonPropertyName("capabilityStatus")]
    public DetectorModelCapabilityStatus? CapabilityStatus { get; init; }

    [JsonPropertyName("nextCheckAt")]
    public DateTimeOffset? NextCheckAt { get; init; }
}

public sealed record ChannelReliabilityRuntimeSnapshot
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("phase")]
    public ChannelReliabilityRunPhase Phase { get; init; } = ChannelReliabilityRunPhase.Idle;

    [JsonPropertyName("runId")]
    public string? RunId { get; init; }

    [JsonPropertyName("trigger")]
    public ChannelReliabilityTrigger? Trigger { get; init; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset? StartedAt { get; init; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset? CompletedAt { get; init; }

    [JsonPropertyName("nextCheckAt")]
    public DateTimeOffset? NextCheckAt { get; init; }

    [JsonPropertyName("selectedKeyCount")]
    public int SelectedKeyCount { get; init; }

    [JsonPropertyName("totalProbeCount")]
    public int TotalProbeCount { get; init; }

    [JsonPropertyName("completedProbeCount")]
    public int CompletedProbeCount { get; init; }

    [JsonPropertyName("failedProbeCount")]
    public int FailedProbeCount { get; init; }

    [JsonPropertyName("timelineTruncated")]
    public bool TimelineTruncated { get; init; }

    [JsonPropertyName("lastEventSequence")]
    public long LastEventSequence { get; init; }

    [JsonPropertyName("probes")]
    public IReadOnlyList<ChannelReliabilityProbeProgress> Probes { get; init; } = [];

    [JsonPropertyName("events")]
    public IReadOnlyList<ChannelReliabilityAuditEvent> Events { get; init; } = [];
}
