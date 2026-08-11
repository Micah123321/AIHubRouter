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

        if (executionFailed)
        {
            return Failure(keyId, groupId, model, DetectorErrorCategory.Unknown,
                ChannelReliabilityStatus.Unavailable, checkedAt);
        }


        if (exitCode != 0)
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
                ChannelReliabilityStatus.EvidenceInsufficient, checkedAt);
        }

        var status = response.Status.Trim().ToLowerInvariant();
        if (status == "complete")
        {
            var verdict = ParseVerdict(response.OverallVerdict);
            if (verdict is null || !IsCompleteEvidenceConsistent(response, verdict.Value, model))
            {
                return Failure(keyId, groupId, model, DetectorErrorCategory.InvalidResponse,
                    ChannelReliabilityStatus.EvidenceInsufficient, checkedAt, response.Official,
                    response.ClaimedModel, VerdictTitle(DetectorVerdict.EvidenceInsufficient),
                    response.NetworkSummary, response.EvidenceSummary);
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
                ErrorCategory = DetectorErrorCategory.None,
                CheckedAt = checkedAt,
                Official = response.Official,
                ClaimedModel = response.ClaimedModel,
                Title = VerdictTitle(verdict.Value),
                NetworkSummary = response.NetworkSummary,
                EvidenceSummary = response.EvidenceSummary
            };
        }

        if (status == "evidence_insufficient")
        {
            return Failure(keyId, groupId, model, DetectorErrorCategory.EvidenceInsufficient,
                ChannelReliabilityStatus.EvidenceInsufficient, checkedAt, response.Official,
                response.ClaimedModel, VerdictTitle(DetectorVerdict.EvidenceInsufficient), response.NetworkSummary,
                response.EvidenceSummary);
        }

        if (status == "error")
        {
            var category = ChannelReliabilityWorkerProtocol.ParseErrorCategory(response.ErrorCode);
            return Failure(keyId, groupId, model, category,
                category == DetectorErrorCategory.EvidenceInsufficient
                    ? ChannelReliabilityStatus.EvidenceInsufficient
                    : ChannelReliabilityStatus.Unavailable,
                checkedAt, response.Official, response.ClaimedModel,
                VerdictTitle(DetectorVerdict.EvidenceInsufficient),
                response.NetworkSummary, response.EvidenceSummary);
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
        if (response.ErrorCode is not null ||
            network.LogicalTasks <= 0 ||
            network.LogicalCompleted != network.LogicalTasks ||
            network.Successful != network.LogicalTasks ||
            network.FinalErrors != 0 ||
            network.Cancelled != 0 ||
            network.InFlight != 0 ||
            network.ErrorCategories.Count != 0 ||
            !evidence.VerdictAvailable ||
            evidence.EvidenceInsufficient ||
            evidence.HardVerdict != ChannelReliabilityRules.IsHardVerdict(verdict) ||
            response.Official != true ||
            !string.Equals(
                response.ClaimedModel,
                DetectorModelNames.ToWorkerModel(model),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return verdict switch
        {
            DetectorVerdict.Passed => string.Equals(evidence.JuiceState, "juice_pass", StringComparison.Ordinal),
            DetectorVerdict.PossibleNonGpt => string.Equals(evidence.JuiceState, "juice_all_unsuccessful", StringComparison.Ordinal),
            DetectorVerdict.JuiceMixed => string.Equals(evidence.JuiceState, "juice_mixed", StringComparison.Ordinal),
            DetectorVerdict.ProbabilityOnlyMixed =>
                evidence.ProbabilityEnabled && evidence.ProbabilityFormalEligible == true,
            DetectorVerdict.EvidenceInsufficient =>
                string.Equals(evidence.JuiceState, "juice_pass", StringComparison.Ordinal) &&
                evidence.ProbabilityEnabled && evidence.ProbabilityFormalEligible != true,
            _ => false
        };
    }

    private static DetectorVerdict? ParseVerdict(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "通过" or "passed" => DetectorVerdict.Passed,
            "可能非gpt" or "possible_non_gpt" => DetectorVerdict.PossibleNonGpt,
            "juice混用" or "juice_mixed" => DetectorVerdict.JuiceMixed,
            "仅概率探针混用" or "probability_only_mixed" =>
                DetectorVerdict.ProbabilityOnlyMixed,
            "juice通过但概率探针证据不足" or "evidence_insufficient" =>
                DetectorVerdict.EvidenceInsufficient,
            _ => null
        };

    private static string VerdictTitle(DetectorVerdict verdict) => verdict switch
    {
        DetectorVerdict.Passed => "通过",
        DetectorVerdict.PossibleNonGpt => "可能非GPT",
        DetectorVerdict.JuiceMixed => "Juice混用",
        DetectorVerdict.ProbabilityOnlyMixed => "仅概率探针混用",
        _ => "未形成正式结论"
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
        DetectorEvidenceSummary? evidenceSummary = null) => new()
        {
            KeyId = keyId,
            GroupId = groupId,
            Model = model,
            Status = status,
            Verdict = DetectorVerdict.EvidenceInsufficient,
            ErrorCategory = category,
            CheckedAt = checkedAt,
            Official = official,
            ClaimedModel = claimedModel,
            Title = title,
            NetworkSummary = networkSummary,
            EvidenceSummary = evidenceSummary
        };
}
