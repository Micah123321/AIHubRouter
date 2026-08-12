namespace AIHubRouter.Core;

/// <summary>
/// Coordinates periodic, per-key reliability checks and local group quarantine.
/// </summary>
public sealed class ChannelReliabilityMonitor : IDisposable
{
    private readonly PersistentAppSettings _settings;
    private readonly PersistentCredentials _credentials;
    private readonly IChannelReliabilityDetector _detector;
    private readonly IChannelQuarantineStore _quarantineStore;
    private readonly ChannelReliabilityRuntime _runtime;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ChannelReliabilityLedger _ledger;
    private readonly Dictionary<DetectionTarget, DateTimeOffset> _lastCheckedAt;
    private readonly Dictionary<DetectionTarget, DetectorResult> _latestModelResults;
    private readonly Dictionary<long, ChannelReliabilityResult> _latestResults = [];
    private bool _disposed;

    public ChannelReliabilityMonitor(
        PersistentAppSettings settings,
        PersistentCredentials credentials,
        IChannelReliabilityDetector detector,
        IChannelQuarantineStore quarantineStore,
        Func<DateTimeOffset>? utcNow = null,
        ChannelReliabilityRuntime? runtime = null,
        ChannelReliabilityLedger? ledger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _quarantineStore = quarantineStore ?? throw new ArgumentNullException(nameof(quarantineStore));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _runtime = runtime ?? new ChannelReliabilityRuntime();
        _ledger = ledger ?? new ChannelReliabilityLedger();
        _lastCheckedAt = _ledger.LastCheckedAt;
        _latestModelResults = _ledger.LatestModelResults;
    }

    public ChannelReliabilityRuntimeSnapshot RuntimeSnapshot => _runtime.Snapshot;

    public ChannelQuarantineSnapshot GetSnapshot(DateTimeOffset? now = null)
    {
        var instant = (now ?? _utcNow()).ToUniversalTime();
        var records = _quarantineStore.LoadLatest();
        return new ChannelQuarantineSnapshot
        {
            CapturedAt = instant,
            Records = records
        };
    }

    public async Task<ChannelReliabilityCycleResult> CheckAsync(
        IReadOnlyList<ApiKeyInfo> selectedKeys,
        IReadOnlyDictionary<long, IReadOnlyDictionary<string, string>> modelHealthByGroup,
        IReadOnlyList<GroupInfo>? groups = null,
        bool dryRun = false,
        bool force = false,
        Func<long, ApiKeyInfo?>? currentKeyResolver = null,
        ChannelReliabilityTrigger trigger = ChannelReliabilityTrigger.Scheduled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedKeys);
        ArgumentNullException.ThrowIfNull(modelHealthByGroup);
        ThrowIfDisposed();

        var startedAt = _utcNow().ToUniversalTime();
        if (!_settings.ReliabilityDetectionEnabled)
        {
            _runtime.SetDisabled(startedAt);
            return BuildCycleResult(
                enabled: false,
                startedAt,
                startedAt,
                selectedKeys,
                groups,
                GetSnapshot(startedAt));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var runStarted = false;
        try
        {
            var now = _utcNow().ToUniversalTime();
            var uniqueKeys = selectedKeys
                .Where(key => key.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
                .GroupBy(key => key.Id)
                .Select(group => group.First())
                .ToArray();
            var snapshot = GetSnapshot(now);
            _runtime.BeginRun(trigger, now, uniqueKeys.Length);
            runStarted = true;

            foreach (var key in uniqueKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await CheckKeyAsync(
                    key,
                    modelHealthByGroup,
                    snapshot,
                    now,
                    dryRun,
                    force,
                    currentKeyResolver,
                    cancellationToken).ConfigureAwait(false);
                _latestResults[key.Id] = result;
                foreach (var modelResult in result.ModelResults)
                {
                    var target = DetectionTarget.Create(key.Id, key.GroupId, modelResult.Model);
                    _latestModelResults[target] = modelResult;
                    _lastCheckedAt[target] = modelResult.CheckedAt ?? now;
                }

                if (!dryRun && result.Quarantine is { } quarantine &&
                    !snapshot.Records.Any(record =>
                        record.GroupId == quarantine.GroupId &&
                        record.IsActiveAt(result.CheckedAt ?? now)))
                {
                    snapshot = snapshot with
                    {
                        CapturedAt = result.CheckedAt ?? now,
                        Records = snapshot.Records
                            .Where(record => record.GroupId != quarantine.GroupId)
                            .Append(quarantine)
                            .ToArray()
                    };
                }
            }

            var completedAt = _utcNow().ToUniversalTime();
            snapshot = snapshot with { CapturedAt = completedAt };
            var cycle = BuildCycleResult(
                enabled: true,
                startedAt,
                completedAt,
                uniqueKeys,
                groups,
                snapshot);
            _runtime.SetNextCheckAt(NextCheckAt(uniqueKeys, completedAt));
            _runtime.CompleteRun(cycle, completedAt);
            return BuildCycleResult(
                enabled: true,
                startedAt,
                completedAt,
                uniqueKeys,
                groups,
                snapshot);
        }
        catch (OperationCanceledException)
        {
            if (runStarted)
            {
                _runtime.Abort(ChannelReliabilityRunPhase.Cancelled, _utcNow().ToUniversalTime());
            }

            throw;
        }
        catch
        {
            if (runStarted)
            {
                _runtime.Abort(ChannelReliabilityRunPhase.Failed, _utcNow().ToUniversalTime());
            }

            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ChannelReliabilityResult> CheckKeyAsync(
        ApiKeyInfo key,
        IReadOnlyDictionary<long, IReadOnlyDictionary<string, string>> modelHealthByGroup,
        ChannelQuarantineSnapshot snapshot,
        DateTimeOffset now,
        bool dryRun,
        bool force,
        Func<long, ApiKeyInfo?>? currentKeyResolver,
        CancellationToken cancellationToken)
    {
        var binding = (_settings.DetectorBindings ?? [])
            .FirstOrDefault(candidate => candidate.KeyId == key.Id && candidate.Enabled);
        var hasBinding = binding is not null;
        var detectorApiKeys = _credentials.DetectorApiKeys ?? [];
        var hasCredential = detectorApiKeys.TryGetValue(key.Id, out var apiKey) &&
            !string.IsNullOrWhiteSpace(apiKey);

        if (key.GroupId is not > 0)
        {
            _runtime.SkipKey(key, ChannelReliabilityStatus.Unconfigured, now,
                ChannelReliabilitySkipReason.MissingGroup);
            return new ChannelReliabilityResult
            {
                KeyId = key.Id,
                GroupId = key.GroupId,
                Status = ChannelReliabilityStatus.Unconfigured,
                CheckedAt = now,
                GroupChanged = false
            };
        }

        if (!hasBinding || !hasCredential)
        {
            _runtime.SkipKey(key, ChannelReliabilityStatus.Unconfigured, now,
                hasBinding
                    ? ChannelReliabilitySkipReason.MissingCredential
                    : ChannelReliabilitySkipReason.MissingBinding);
            return new ChannelReliabilityResult
            {
                KeyId = key.Id,
                GroupId = key.GroupId,
                Status = ChannelReliabilityStatus.Unconfigured,
                CheckedAt = now,
                GroupChanged = false
            };
        }

        var models = ChannelReliabilityRules.SelectProbeModels(binding!);
        if (models.Count == 0)
        {
            _runtime.SkipKey(key, ChannelReliabilityStatus.EvidenceInsufficient, now,
                ChannelReliabilitySkipReason.NoModels);
            return new ChannelReliabilityResult
            {
                KeyId = key.Id,
                GroupId = key.GroupId,
                Status = ChannelReliabilityStatus.EvidenceInsufficient,
                ProbedModels = [],
                CheckedAt = now,
                GroupChanged = false
            };
        }

        var modelResults = new List<DetectorResult>(models.Count);
        var probedModels = new List<string>(models.Count);
        foreach (var model in models)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = DetectionTarget.Create(key.Id, key.GroupId, model);
            // Keep the configured model list visible even when this cycle uses the ledger cache.
            probedModels.Add(model);
            var capabilityStatus = CapabilityStatus(modelHealthByGroup, target);
            if (!force && _lastCheckedAt.TryGetValue(target, out var lastChecked) &&
                now - lastChecked < DetectionInterval())
            {
                var cachedResult = _latestModelResults[target];
                modelResults.Add(cachedResult);
                _runtime.SkipModel(
                    key,
                    model,
                    cachedResult.Status,
                    capabilityStatus,
                    lastChecked + DetectionInterval(),
                    now);
                continue;
            }

            _runtime.QueueModel(key, model, _utcNow().ToUniversalTime(), capabilityStatus);
            _runtime.StartModel(key, model, _utcNow().ToUniversalTime());
            DetectorResult modelResult;
            try
            {
                modelResult = await _detector.DetectAsync(
                    binding,
                    model,
                    apiKey,
                    key.GroupId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                modelResult = new DetectorResult
                {
                    KeyId = key.Id,
                    GroupId = key.GroupId,
                    Model = model,
                    Status = ChannelReliabilityStatus.Unavailable,
                    Verdict = DetectorVerdict.EvidenceInsufficient,
                    ErrorCategory = DetectorErrorCategory.Timeout,
                    CheckedAt = now
                };
            }
            catch (Exception)
            {
                // A detector implementation is an optional boundary; its failure must not break routing.
                modelResult = new DetectorResult
                {
                    KeyId = key.Id,
                    GroupId = key.GroupId,
                    Model = model,
                    Status = ChannelReliabilityStatus.Unavailable,
                    Verdict = DetectorVerdict.EvidenceInsufficient,
                    ErrorCategory = DetectorErrorCategory.Unknown,
                    CheckedAt = now
                };
            }
            modelResults.Add(modelResult with
            {
                KeyId = key.Id,
                GroupId = key.GroupId,
                Model = DetectorModelNames.Normalize(model) ?? model,
                CheckedAt = modelResult.CheckedAt ?? now
            });
            _runtime.CompleteModel(key, modelResults[^1], _utcNow().ToUniversalTime());
        }

        var decisionAt = _utcNow().ToUniversalTime();
        var latestCheckedAt = modelResults.Max(result => result.CheckedAt) ?? decisionAt;
        var current = currentKeyResolver?.Invoke(key.Id);
        var groupChanged = currentKeyResolver is not null &&
            (current is null ||
             !current.Status.Equals("active", StringComparison.OrdinalIgnoreCase) ||
             current.GroupId != key.GroupId);
        if (groupChanged)
        {
            return new ChannelReliabilityResult
            {
                KeyId = key.Id,
                GroupId = key.GroupId,
                Status = ChannelReliabilityStatus.EvidenceInsufficient,
                Verdict = null,
                OutcomeCode = FirstOutcomeCode(modelResults),
                ProbedModels = probedModels,
                ModelResults = modelResults,
                CheckedAt = latestCheckedAt,
                GroupChanged = true
            };
        }

        var hardResult = modelResults.All(result => result.ErrorCategory == DetectorErrorCategory.None)
            ? modelResults.FirstOrDefault(result => result.IsQuarantineEligible)
            : null;
        if (hardResult is { } hard)
        {
            var existing = snapshot.Records.FirstOrDefault(record =>
                record.GroupId == key.GroupId.Value && record.IsActiveAt(decisionAt));
            var quarantine = existing ?? new ChannelQuarantineRecord
            {
                GroupId = key.GroupId.Value,
                QuarantinedAt = decisionAt,
                ExpiresAt = decisionAt.AddHours(Math.Clamp(_settings.ReliabilityQuarantineHours, 1, 168)),
                Verdict = hard.Verdict,
                SourceKeyId = key.Id,
                SourceModel = hard.Model
            };
            if (existing is null && !dryRun)
            {
                _quarantineStore.Save(quarantine);
            }
            _runtime.RecordQuarantine(key, quarantine, applied: !dryRun || existing is not null,
                _utcNow().ToUniversalTime());

            return new ChannelReliabilityResult
            {
                KeyId = key.Id,
                GroupId = key.GroupId,
                Status = existing is not null || !dryRun
                    ? ChannelReliabilityStatus.Quarantined
                    : ChannelReliabilityStatus.EvidenceInsufficient,
                Verdict = hard.Verdict,
                OutcomeCode = hard.OutcomeCode == DetectorOutcomeCode.Unknown ? null : hard.OutcomeCode,
                ProbedModels = probedModels,
                ModelResults = modelResults,
                CheckedAt = latestCheckedAt,
                GroupChanged = false,
                Quarantine = existing is not null || !dryRun ? quarantine : null,
                WouldQuarantine = existing is null && dryRun
            };
        }

        var activeQuarantine = snapshot.Records.FirstOrDefault(record =>
            record.GroupId == key.GroupId.Value && record.IsActiveAt(decisionAt));
        var summaryResults = ChannelReliabilityRules.SelectSummaryResults(modelResults, binding!);
        return new ChannelReliabilityResult
        {
            KeyId = key.Id,
            GroupId = key.GroupId,
            Status = activeQuarantine is not null
                ? ChannelReliabilityStatus.Quarantined
                : ResolveStatusWithoutQuarantine(
                    ChannelReliabilityStatus.EvidenceInsufficient,
                    summaryResults),
            Verdict = activeQuarantine?.Verdict ?? summaryResults
                .Select(result => result.Verdict)
                .FirstOrDefault(verdict => verdict != DetectorVerdict.EvidenceInsufficient),
            OutcomeCode = activeQuarantine is null ? FirstOutcomeCode(summaryResults) : null,
            ProbedModels = probedModels,
            ModelResults = modelResults,
            CheckedAt = latestCheckedAt,
            GroupChanged = false,
            Quarantine = activeQuarantine
        };
    }

    private ChannelReliabilityCycleResult BuildCycleResult(
        bool enabled,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        IReadOnlyList<ApiKeyInfo> selectedKeys,
        IReadOnlyList<GroupInfo>? groups,
        ChannelQuarantineSnapshot snapshot)
    {
        var activeByGroup = snapshot.Records
            .Where(record => record.IsActiveAt(completedAt))
            .GroupBy(record => record.GroupId)
            .ToDictionary(group => group.Key, group => group.First());
        var results = selectedKeys
            .Select(key => _latestResults.TryGetValue(key.Id, out var result) && result.GroupId == key.GroupId
                ? result
                : new ChannelReliabilityResult
                {
                    KeyId = key.Id,
                    GroupId = key.GroupId,
                    Status = ChannelReliabilityStatus.EvidenceInsufficient,
                    CheckedAt = null,
                    GroupChanged = false
                })
            .Select(result => NormalizeQuarantine(
                result,
                activeByGroup,
                (_settings.DetectorBindings ?? [])
                    .FirstOrDefault(binding => binding.KeyId == result.KeyId)))
            .ToArray();
        var groupNames = (groups ?? [])
            .GroupBy(group => group.Id)
            .ToDictionary(group => group.Key, group => group.First().Name);
        var groupSummaries = results
            .Where(result => result.GroupId is > 0)
            .GroupBy(result => result.GroupId!.Value)
            .Select(group =>
            {
                var groupResults = group.ToArray();
                var quarantine = activeByGroup.GetValueOrDefault(group.Key);
                return new ChannelReliabilityGroupSummary
                {
                    GroupId = group.Key,
                    GroupName = groupNames.TryGetValue(group.Key, out var name) ? name : string.Empty,
                    Status = quarantine is not null
                        ? ChannelReliabilityStatus.Quarantined
                        : ChannelReliabilityRules.ResolveKeyStatus(groupResults),
                    Models = groupResults
                        .SelectMany(result => result.ProbedModels)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order()
                        .ToArray(),
                    Verdict = groupResults
                        .Select(result => result.Verdict)
                        .FirstOrDefault(verdict => verdict is not null),
                    SourceKeyId = quarantine?.SourceKeyId,
                    QuarantinedUntil = quarantine?.ExpiresAt
                };
            })
            .OrderBy(summary => summary.GroupId)
            .ToArray();
        var detectorApiKeys = _credentials.DetectorApiKeys ?? [];
        var keySummaries = selectedKeys
            .Select(key =>
            {
                var result = results.FirstOrDefault(candidate => candidate.KeyId == key.Id);
                var binding = (_settings.DetectorBindings ?? [])
                    .FirstOrDefault(candidate => candidate.KeyId == key.Id);
                var quarantine = key.GroupId is > 0
                    ? activeByGroup.GetValueOrDefault(key.GroupId.Value)
                    : null;
                return new ChannelReliabilityKeySummary
                {
                    KeyId = key.Id,
                    KeyName = key.Name,
                    GroupId = key.GroupId,
                    HasDetectorBinding = binding is { Enabled: true },
                    HasDetectorCredential = detectorApiKeys.TryGetValue(key.Id, out var secret) &&
                        !string.IsNullOrWhiteSpace(secret),
                    Status = quarantine is not null
                        ? ChannelReliabilityStatus.Quarantined
                        : result?.Status ?? FallbackStatus(key),
                    Verdict = result?.Verdict,
                    Models = result?.ProbedModels ?? [],
                    LastCheckedAt = result?.CheckedAt,
                    NextCheckAt = NextCheckAtFor(key, result),
                    QuarantinedUntil = quarantine?.ExpiresAt
                };
            })
            .ToArray();

        return new ChannelReliabilityCycleResult
        {
            Enabled = enabled,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            Runtime = _runtime.Snapshot,
            Results = results,
            Keys = keySummaries,
            Groups = groupSummaries,
            Quarantine = snapshot
        };
    }

    private DateTimeOffset? NextCheckAtFor(ApiKeyInfo key, ChannelReliabilityResult? result)
    {
        if (result is null || key.GroupId is not > 0)
        {
            return null;
        }

        var dueTimes = result.ProbedModels
            .Select(model => DetectionTarget.Create(key.Id, key.GroupId, model))
            .Where(target => _lastCheckedAt.ContainsKey(target))
            .Select(target => _lastCheckedAt[target] + DetectionInterval())
            .ToArray();
        return dueTimes.Length == 0 ? null : dueTimes.Min();
    }

    private static DetectorOutcomeCode? FirstOutcomeCode(IEnumerable<DetectorResult> results)
    {
        var outcome = results
            .Select(result => result.OutcomeCode)
            .FirstOrDefault(code => code != DetectorOutcomeCode.Unknown);
        return outcome == DetectorOutcomeCode.Unknown ? null : outcome;
    }

    private static ChannelReliabilityResult NormalizeQuarantine(
        ChannelReliabilityResult result,
        IReadOnlyDictionary<long, ChannelQuarantineRecord> activeByGroup,
        DetectorBinding? binding)
    {
        if (result.GroupId is > 0 && activeByGroup.TryGetValue(result.GroupId.Value, out var active))
        {
            return result with
            {
                Status = ChannelReliabilityStatus.Quarantined,
                Verdict = active.Verdict,
                OutcomeCode = null,
                Quarantine = active
            };
        }

        if (result.Status != ChannelReliabilityStatus.Quarantined && result.Quarantine is null)
        {
            return result;
        }

        var summaryResults = binding is null
            ? result.ModelResults
            : ChannelReliabilityRules.SelectSummaryResults(result.ModelResults, binding);
        return result with
        {
            Status = ResolveStatusWithoutQuarantine(result.Status, summaryResults),
            Verdict = summaryResults
                .Select(item => item.Verdict)
                .FirstOrDefault(verdict => verdict != DetectorVerdict.EvidenceInsufficient),
            OutcomeCode = FirstOutcomeCode(summaryResults),
            Quarantine = null
        };
    }

    private static ChannelReliabilityStatus ResolveStatusWithoutQuarantine(
        ChannelReliabilityStatus fallbackStatus,
        IReadOnlyCollection<DetectorResult> summaryResults)
    {
        if (summaryResults.Any(item => item.Status == ChannelReliabilityStatus.Unavailable))
        {
            return ChannelReliabilityStatus.Unavailable;
        }

        if (summaryResults.Count > 0 && summaryResults.All(item =>
                item.Status == ChannelReliabilityStatus.Passed && item.Verdict == DetectorVerdict.Passed))
        {
            return ChannelReliabilityStatus.Passed;
        }

        return fallbackStatus == ChannelReliabilityStatus.Unconfigured
            ? ChannelReliabilityStatus.Unconfigured
            : ChannelReliabilityStatus.EvidenceInsufficient;
    }

    private ChannelReliabilityStatus FallbackStatus(ApiKeyInfo key)
    {
        if (key.GroupId is not > 0)
        {
            return ChannelReliabilityStatus.Unconfigured;
        }

        var hasBinding = (_settings.DetectorBindings ?? [])
            .Any(binding => binding.KeyId == key.Id && binding.Enabled);
        var hasCredential = (_credentials.DetectorApiKeys ?? [])
            .TryGetValue(key.Id, out var secret) && !string.IsNullOrWhiteSpace(secret);
        return hasBinding && hasCredential
            ? ChannelReliabilityStatus.EvidenceInsufficient
            : ChannelReliabilityStatus.Unconfigured;
    }

    private static TimeSpan DetectionInterval() => TimeSpan.FromHours(1);

    private DateTimeOffset NextCheckAt(IReadOnlyList<ApiKeyInfo> keys, DateTimeOffset now)
    {
        var dueTimes = keys
            .SelectMany(DetectionTargetsFor)
            .Select(target => _lastCheckedAt.TryGetValue(target, out var checkedAt)
                ? checkedAt + DetectionInterval()
                : now)
            .ToArray();
        return dueTimes.Length == 0 ? now + DetectionInterval() : dueTimes.Min();
    }

    private static DetectorModelCapabilityStatus CapabilityStatus(
        IReadOnlyDictionary<long, IReadOnlyDictionary<string, string>> healthByGroup,
        DetectionTarget target)
    {
        if (!healthByGroup.TryGetValue(target.GroupId, out var health))
        {
            return DetectorModelCapabilityStatus.Missing;
        }

        var sample = health.FirstOrDefault(entry =>
            string.Equals(entry.Key, target.Model, StringComparison.OrdinalIgnoreCase));
        return sample.Key is null
            ? DetectorModelCapabilityStatus.Missing
            : ChannelReliabilityRules.ParseCapabilityStatus(sample.Value);
    }

    private IReadOnlyList<DetectionTarget> DetectionTargetsFor(ApiKeyInfo key)
    {
        if (key.GroupId is not > 0)
        {
            return [];
        }

        var binding = (_settings.DetectorBindings ?? [])
            .FirstOrDefault(candidate => candidate.KeyId == key.Id && candidate.Enabled);
        return binding is null
            ? []
            : ChannelReliabilityRules.SelectProbeModels(binding)
                .Select(model => DetectionTarget.Create(key.Id, key.GroupId, model))
                .ToArray();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ChannelReliabilityMonitor));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }
}

internal readonly record struct DetectionTarget(long KeyId, long GroupId, string Model)
{
    public static DetectionTarget Create(long keyId, long? groupId, string model) =>
        new(keyId, groupId ?? 0, DetectorModelNames.Normalize(model) ?? model);
}

public sealed class ChannelReliabilityLedger
{
    internal Dictionary<DetectionTarget, DateTimeOffset> LastCheckedAt { get; } = [];
    internal Dictionary<DetectionTarget, DetectorResult> LatestModelResults { get; } = [];
}
