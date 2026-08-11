namespace AIHubRouter.Core;

internal static class ChannelReliabilityResultMapper
{
    public static DetectorResult MapProcessResult(
        long keyId,
        long? groupId,
        string model,
        DateTimeOffset checkedAt,
        BoundedOutput stdout,
        int exitCode,
        bool executionFailed,
        bool timedOut,
        bool cancelled)
    {
        if (cancelled)
        {
            return Failure(keyId, groupId, model, DetectorErrorCategory.Cancelled,
                ChannelReliabilityStatus.Unavailable, checkedAt);
        }

        if (timedOut)
        {
            return Failure(keyId, groupId, model, DetectorErrorCategory.Timeout,
                ChannelReliabilityStatus.Unavailable, checkedAt);
        }

        if (stdout.Truncated)
        {
            return Failure(keyId, groupId, model, DetectorErrorCategory.StreamTruncated,
                ChannelReliabilityStatus.Unavailable, checkedAt);
        }

        if (executionFailed || exitCode != 0)
        {
            return Failure(keyId, groupId, model, DetectorErrorCategory.Unknown,
                ChannelReliabilityStatus.Unavailable, checkedAt);
        }

        if (!ChannelReliabilityWorkerProtocol.TryReadLastResponse(stdout.Text, out var response))
        {
            return Failure(keyId, groupId, model, DetectorErrorCategory.InvalidResponse,
                ChannelReliabilityStatus.EvidenceInsufficient, checkedAt);
        }

        if (response.ClaimedModel is not null &&
            !string.Equals(response.ClaimedModel, DetectorModelNames.ToWorkerModel(model),
                StringComparison.OrdinalIgnoreCase))
        {
            return Failure(keyId, groupId, model, DetectorErrorCategory.InvalidResponse,
                ChannelReliabilityStatus.EvidenceInsufficient, checkedAt,
                response.Official, response.ClaimedModel, response.TitleCn ?? response.OverallVerdict,
                response.NetworkSummary, response.EvidenceSummary, response.OutcomeCode,
                response.JuiceState, response.FingerprintState, response.FingerprintModel);
        }

        var status = response.Status.Trim().ToLowerInvariant();
        if (status == "complete")
        {
            var verdict = MapOutcome(response.OutcomeCode);
            if (verdict is null || !IsCompleteEvidenceConsistent(response, verdict.Value, model))
            {
                return Failure(keyId, groupId, model, DetectorErrorCategory.InvalidResponse,
                    ChannelReliabilityStatus.EvidenceInsufficient, checkedAt, response.Official,
                    response.ClaimedModel, "未形成正式结论", response.NetworkSummary,
                    response.EvidenceSummary, response.OutcomeCode, response.JuiceState,
                    response.FingerprintState, response.FingerprintModel);
            }

            return new DetectorResult
            {
                KeyId = keyId,
                GroupId = groupId,
                Model = model,
                Status = verdict == DetectorVerdict.Passed
                    ? ChannelReliabilityStatus.Passed
                    : ChannelReliabilityStatus.EvidenceInsufficient,
                Verdict = verdict.Value,
                OutcomeCode = response.OutcomeCode,
                ErrorCategory = DetectorErrorCategory.None,
                CheckedAt = checkedAt,
                Official = response.Official,
                ClaimedModel = response.ClaimedModel,
                JuiceState = response.JuiceState,
                FingerprintState = response.FingerprintState,
                FingerprintModel = response.FingerprintModel,
                Title = response.TitleCn ?? response.OverallVerdict ?? "未形成正式结论",
                NetworkSummary = response.NetworkSummary,
                EvidenceSummary = response.EvidenceSummary
            };
        }

        if (status == "evidence_insufficient")
        {
            return Failure(keyId, groupId, model, DetectorErrorCategory.EvidenceInsufficient,
                ChannelReliabilityStatus.EvidenceInsufficient, checkedAt, response.Official,
                response.ClaimedModel, "未形成正式结论", response.NetworkSummary,
                response.EvidenceSummary, response.OutcomeCode, response.JuiceState,
                response.FingerprintState, response.FingerprintModel);
        }

        if (status == "error")
        {
            var category = ChannelReliabilityWorkerProtocol.ParseErrorCategory(response.ErrorCode);
            return Failure(keyId, groupId, model, category,
                category == DetectorErrorCategory.EvidenceInsufficient
                    ? ChannelReliabilityStatus.EvidenceInsufficient
                    : ChannelReliabilityStatus.Unavailable,
                checkedAt, response.Official, response.ClaimedModel, "未形成正式结论",
                response.NetworkSummary, response.EvidenceSummary, response.OutcomeCode,
                response.JuiceState, response.FingerprintState, response.FingerprintModel);
        }

        return Failure(keyId, groupId, model, DetectorErrorCategory.InvalidResponse,
            ChannelReliabilityStatus.EvidenceInsufficient, checkedAt);
    }

    private static bool IsCompleteEvidenceConsistent(
        ChannelReliabilityWorkerProtocol.WorkerResponse response,
        DetectorVerdict verdict,
        string model)
    {
        var network = response.NetworkSummary;
        var evidence = response.EvidenceSummary;
        if (response.ReportSchemaVersion != 3 ||
            evidence.ReportSchemaVersion != 3 ||
            response.ErrorCode is not null ||
            network.LogicalTasks <= 0 ||
            network.LogicalCompleted != network.LogicalTasks ||
            network.Successful != network.LogicalTasks ||
            network.FinalErrors != 0 ||
            network.Cancelled != 0 ||
            network.InFlight != 0 ||
            network.ErrorCategories.Count != 0 ||
            !evidence.VerdictAvailable ||
            evidence.OutcomeCode != response.OutcomeCode ||
            evidence.JuiceState != response.JuiceState ||
            evidence.FingerprintState != response.FingerprintState ||
            !string.Equals(evidence.FingerprintModel, response.FingerprintModel, StringComparison.OrdinalIgnoreCase) ||
            evidence.HardVerdict != ChannelReliabilityRules.IsHardVerdict(verdict) ||
            evidence.EvidenceInsufficient != IsEvidenceInsufficient(response.OutcomeCode) ||
            evidence.FingerprintFormalEligible != (response.FingerprintState == "strong_match") ||
            response.Official != true ||
            !string.Equals(response.ClaimedModel, DetectorModelNames.ToWorkerModel(model),
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(response.TitleCn ?? response.OverallVerdict))
        {
            return false;
        }

        return MatchesOutcome(response, verdict);
    }

    private static bool MatchesOutcome(
        ChannelReliabilityWorkerProtocol.WorkerResponse response,
        DetectorVerdict verdict) => response.OutcomeCode switch
        {
            DetectorOutcomeCode.JuicePassFingerprintStrong =>
                verdict == DetectorVerdict.Passed && response.JuiceState == "pass" &&
                response.FingerprintState == "strong_match" && response.FingerprintModel is not null,
            DetectorOutcomeCode.JuicePassFingerprintUnclear =>
                verdict == DetectorVerdict.Passed && response.JuiceState == "pass" &&
                response.FingerprintState == "unclear" && response.FingerprintModel is null,
            DetectorOutcomeCode.JuiceMismatchFingerprintStrong =>
                verdict == DetectorVerdict.JuiceMixed && response.JuiceState == "mismatch" &&
                response.FingerprintState == "strong_match" && response.FingerprintModel is not null,
            DetectorOutcomeCode.JuiceMismatchFingerprintUnclear =>
                verdict == DetectorVerdict.JuiceMixed && response.JuiceState == "mismatch" &&
                response.FingerprintState == "unclear" && response.FingerprintModel is null,
            DetectorOutcomeCode.JuiceInsufficientFingerprintStrong =>
                verdict == DetectorVerdict.EvidenceInsufficient && response.JuiceState == "insufficient" &&
                response.FingerprintState == "strong_match" && response.FingerprintModel is not null,
            DetectorOutcomeCode.JuiceInsufficientFingerprintUnclear =>
                verdict == DetectorVerdict.EvidenceInsufficient && response.JuiceState == "insufficient" &&
                response.FingerprintState == "unclear" && response.FingerprintModel is null,
            DetectorOutcomeCode.PossibleNonGpt =>
                verdict == DetectorVerdict.PossibleNonGpt && response.JuiceState == "possible_non_gpt" &&
                response.FingerprintState == "unclear" && response.FingerprintModel is null,
            _ => false
        };

    private static bool IsEvidenceInsufficient(DetectorOutcomeCode outcomeCode) => outcomeCode is
        DetectorOutcomeCode.JuiceInsufficientFingerprintStrong or
        DetectorOutcomeCode.JuiceInsufficientFingerprintUnclear;

    private static DetectorVerdict? MapOutcome(DetectorOutcomeCode outcomeCode) => outcomeCode switch
    {
        DetectorOutcomeCode.JuicePassFingerprintStrong or
        DetectorOutcomeCode.JuicePassFingerprintUnclear => DetectorVerdict.Passed,
        DetectorOutcomeCode.JuiceMismatchFingerprintStrong or
        DetectorOutcomeCode.JuiceMismatchFingerprintUnclear => DetectorVerdict.JuiceMixed,
        DetectorOutcomeCode.JuiceInsufficientFingerprintStrong or
        DetectorOutcomeCode.JuiceInsufficientFingerprintUnclear => DetectorVerdict.EvidenceInsufficient,
        DetectorOutcomeCode.PossibleNonGpt => DetectorVerdict.PossibleNonGpt,
        _ => null
    };

    private static DetectorResult Failure(
        long keyId,
        long? groupId,
        string model,
        DetectorErrorCategory category,
        ChannelReliabilityStatus status,
        DateTimeOffset checkedAt,
        bool? official = null,
        string? claimedModel = null,
        string? title = null,
        DetectorNetworkSummary? networkSummary = null,
        DetectorEvidenceSummary? evidenceSummary = null,
        DetectorOutcomeCode outcomeCode = DetectorOutcomeCode.Unknown,
        string? juiceState = null,
        string? fingerprintState = null,
        string? fingerprintModel = null) => new()
        {
            KeyId = keyId,
            GroupId = groupId,
            Model = model,
            Status = status,
            Verdict = DetectorVerdict.EvidenceInsufficient,
            OutcomeCode = outcomeCode,
            ErrorCategory = category,
            CheckedAt = checkedAt,
            Official = official,
            ClaimedModel = claimedModel,
            JuiceState = juiceState,
            FingerprintState = fingerprintState,
            FingerprintModel = fingerprintModel,
            Title = title,
            NetworkSummary = networkSummary,
            EvidenceSummary = evidenceSummary
        };
}
