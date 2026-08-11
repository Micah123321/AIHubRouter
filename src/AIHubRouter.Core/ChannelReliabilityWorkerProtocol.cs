using System.Text.Json;

namespace AIHubRouter.Core;

internal static class ChannelReliabilityWorkerProtocol
{
    private const int MaxScalarLength = 4096;

    public static bool TryReadLastResponse(string stdout, out WorkerResponse response)
    {
        response = default!;
        foreach (var line in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            try
            {
                using var document = JsonDocument.Parse(line, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8
                });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var summary = root;
                if (root.TryGetProperty("summary", out var nested) &&
                    nested.ValueKind == JsonValueKind.Object)
                {
                    summary = nested;
                }

                if (!TryString(summary, "status", out var status))
                {
                    continue;
                }

                response = new WorkerResponse(
                    status,
                    TryString(summary, "overall_verdict", out var verdict) ? verdict : null,
                    TryString(summary, "error_code", out var errorCode) ? errorCode : null,
                    TryBoolean(summary, "official", out var official) ? official : null,
                    TryString(summary, "claimed_model", out var claimedModel) ? claimedModel : null,
                    ParseNetworkSummary(summary),
                    ParseEvidenceSummary(summary));
                return true;
            }
            catch (JsonException)
            {
                // Worker logs or malformed lines never become application output.
            }
        }

        return false;
    }

    public static DetectorErrorCategory ParseErrorCategory(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "timeout" => DetectorErrorCategory.Timeout,
            "http_error" => DetectorErrorCategory.HttpError,
            "network_error" => DetectorErrorCategory.NetworkError,
            "processing_error" => DetectorErrorCategory.ProcessingError,
            "truncated_stream" => DetectorErrorCategory.StreamTruncated,
            "evidence_insufficient" => DetectorErrorCategory.EvidenceInsufficient,
            "invalid_input" => DetectorErrorCategory.InvalidResponse,
            _ => DetectorErrorCategory.Unknown
        };

    private static DetectorNetworkSummary ParseNetworkSummary(JsonElement root)
    {
        if (!root.TryGetProperty("network_summary", out var summary) ||
            summary.ValueKind != JsonValueKind.Object)
        {
            return new DetectorNetworkSummary();
        }

        var categories = new List<DetectorErrorCount>();
        if (summary.TryGetProperty("error_categories", out var errors) &&
            errors.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in errors.EnumerateObject().Take(16))
            {
                if (!TryNormalizeErrorCategory(property.Name, out var category) ||
                    !TryBoundedInteger(property.Value, out var count) || count <= 0)
                {
                    continue;
                }

                categories.Add(new DetectorErrorCount { Category = category, Count = count });
            }
        }

        return new DetectorNetworkSummary
        {
            LogicalTasks = ReadBoundedInteger(summary, "logical_tasks"),
            LogicalCompleted = ReadBoundedInteger(summary, "logical_completed"),
            Successful = ReadBoundedInteger(summary, "successful"),
            FinalErrors = ReadBoundedInteger(summary, "final_errors"),
            Cancelled = ReadBoundedInteger(summary, "cancelled"),
            HttpAttempts = ReadBoundedInteger(summary, "http_attempts"),
            Retries = ReadBoundedInteger(summary, "retries"),
            InFlight = ReadBoundedInteger(summary, "in_flight"),
            ErrorCategories = categories
        };
    }

    private static DetectorEvidenceSummary ParseEvidenceSummary(JsonElement root)
    {
        if (!root.TryGetProperty("evidence_summary", out var summary) ||
            summary.ValueKind != JsonValueKind.Object)
        {
            return new DetectorEvidenceSummary();
        }

        return new DetectorEvidenceSummary
        {
            VerdictAvailable = ReadBoolean(summary, "verdict_available"),
            HardVerdict = ReadBoolean(summary, "hard_verdict"),
            JuiceState = NormalizeJuiceState(ReadBoundedString(summary, "juice_state")),
            JuiceValidCompleted = ReadBoundedInteger(summary, "juice_valid_completed"),
            JuiceCurrentSuccess = ReadBoundedInteger(summary, "juice_current_success"),
            JuiceMixed = ReadBoundedInteger(summary, "juice_mixed"),
            JuiceNetworkErrors = ReadBoundedInteger(summary, "juice_network_errors"),
            OutputRequests = ReadBoundedInteger(summary, "output_requests"),
            OutputExact = ReadBoundedInteger(summary, "output_exact"),
            CoverageRequests = ReadBoundedInteger(summary, "coverage_requests"),
            CoverageHardAnomaly = ReadBoolean(summary, "coverage_hard_anomaly"),
            ProbabilityEnabled = ReadBoolean(summary, "probability_enabled"),
            ProbabilityFormalEligible = ReadNullableBoolean(summary, "probability_formal_eligible"),
            EvidenceInsufficient = ReadBoolean(summary, "evidence_insufficient")
        };
    }

    private static int ReadBoundedInteger(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && TryBoundedInteger(value, out var result)
            ? result
            : 0;

    private static bool TryBoundedInteger(JsonElement value, out int result)
    {
        result = 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var candidate))
        {
            return false;
        }

        result = Math.Clamp(candidate, 0, 1_000_000);
        return true;
    }

    private static bool ReadBoolean(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) &&
        value.ValueKind is (JsonValueKind.True or JsonValueKind.False) && value.GetBoolean();

    private static bool? ReadNullableBoolean(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string? ReadBoundedString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() is { Length: > 0 and <= MaxScalarLength } candidate ? candidate : null
            : null;

    private static string NormalizeJuiceState(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "juice_pass" => "juice_pass",
            "juice_mixed" => "juice_mixed",
            "juice_all_unsuccessful" => "juice_all_unsuccessful",
            "data_insufficient" => "data_insufficient",
            _ => "unknown"
        };

    private static bool TryString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var candidate = property.GetString();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > MaxScalarLength)
        {
            return false;
        }

        value = candidate;
        return true;
    }

    private static bool TryBoolean(JsonElement root, string name, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(name, out var property) ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    private static bool TryNormalizeErrorCategory(string value, out string normalized)
    {
        normalized = value.Trim().ToLowerInvariant() switch
        {
            "timeout" => "timeout",
            "http_error" => "http_error",
            "truncated_stream" => "truncated_stream",
            "network_error" => "network_error",
            "processing_error" => "processing_error",
            "evidence_insufficient" => "evidence_insufficient",
            _ => string.Empty
        };
        return normalized.Length > 0;
    }

    internal sealed record WorkerResponse(
        string Status,
        string? OverallVerdict,
        string? ErrorCode,
        bool? Official,
        string? ClaimedModel,
        DetectorNetworkSummary NetworkSummary,
        DetectorEvidenceSummary EvidenceSummary);
}
