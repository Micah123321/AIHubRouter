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
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<long, DateTimeOffset> _lastCheckedAt = [];
    private readonly Dictionary<long, ChannelReliabilityResult> _latestResults = [];
    private bool _disposed;

    public ChannelReliabilityMonitor(
        PersistentAppSettings settings,
        PersistentCredentials credentials,
        IChannelReliabilityDetector detector,
        IChannelQuarantineStore quarantineStore,
        Func<DateTimeOffset>? utcNow = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _quarantineStore = quarantineStore ?? throw new ArgumentNullException(nameof(quarantineStore));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedKeys);
        ArgumentNullException.ThrowIfNull(modelHealthByGroup);
        ThrowIfDisposed();

        var startedAt = _utcNow().ToUniversalTime();
        if (!_settings.ReliabilityDetectionEnabled)
        {
            return BuildCycleResult(
                enabled: false,
                startedAt,
                startedAt,
                selectedKeys,
                groups,
                GetSnapshot(startedAt));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _utcNow().ToUniversalTime();
            var interval = DetectionInterval();
            var uniqueKeys = selectedKeys
                .Where(key => key.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
                .GroupBy(key => key.Id)
                .Select(group => group.First())
                .ToArray();
            var snapshot = GetSnapshot(now);

            foreach (var key in uniqueKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var previous = _latestResults.TryGetValue(key.Id, out var cached)
                    ? cached
                    : null;
                var groupChanged = previous is not null && previous.GroupId != key.GroupId;
                var due = force || groupChanged || !_lastCheckedAt.TryGetValue(key.Id, out var lastChecked) ||
                    now - lastChecked >= interval;
                if (!due)
                {
                    continue;
                }

                var result = await CheckKeyAsync(
                    key,
                    modelHealthByGroup,
                    snapshot,
                    now,
                    dryRun,
                    currentKeyResolver,
                    cancellationToken).ConfigureAwait(false);
                _latestResults[key.Id] = result;
                _lastCheckedAt[key.Id] = now;

                if (!dryRun && result.Quarantine is { } quarantine &&
                    !snapshot.Records.Any(record =>
                        record.GroupId == quarantine.GroupId && record.IsActiveAt(now)))
                {
                    snapshot = snapshot with
                    {
                        Records = snapshot.Records
                            .Where(record => record.GroupId != quarantine.GroupId)
                            .Append(quarantine)
                            .ToArray()
                    };
                }
            }

            return BuildCycleResult(
                enabled: true,
                startedAt,
                _utcNow().ToUniversalTime(),
                uniqueKeys,
                groups,
                snapshot);
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
            return new ChannelReliabilityResult
            {
                KeyId = key.Id,
                GroupId = key.GroupId,
                Status = ChannelReliabilityStatus.Unconfigured,
                CheckedAt = now,
                GroupChanged = false
            };
        }

        if (!modelHealthByGroup.TryGetValue(key.GroupId.Value, out var modelHealth))
        {
            return new ChannelReliabilityResult
            {
                KeyId = key.Id,
                GroupId = key.GroupId,
                Status = ChannelReliabilityStatus.EvidenceInsufficient,
                CheckedAt = now,
                GroupChanged = false
            };
        }

        var models = ChannelReliabilityRules.SelectProbeModels(modelHealth, binding!);
        if (models.Count == 0)
        {
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
        foreach (var model in models)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                Model = DetectorModelNames.Normalize(model) ?? model
            });
        }

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
                ProbedModels = models,
                ModelResults = modelResults,
                CheckedAt = now,
                GroupChanged = true
            };
        }

        var hardResult = modelResults.FirstOrDefault(result => result.IsQuarantineEligible);
        if (hardResult is { } hard)
        {
            var existing = snapshot.Records.FirstOrDefault(record =>
                record.GroupId == key.GroupId.Value && record.IsActiveAt(now));
            var quarantine = existing ?? new ChannelQuarantineRecord
            {
                GroupId = key.GroupId.Value,
                QuarantinedAt = now,
                ExpiresAt = now.AddHours(Math.Clamp(_settings.ReliabilityQuarantineHours, 1, 168)),
                Verdict = hard.Verdict,
                SourceKeyId = key.Id,
                SourceModel = hard.Model
            };
            if (existing is null && !dryRun)
            {
                _quarantineStore.Save(quarantine);
            }

            return new ChannelReliabilityResult
            {
                KeyId = key.Id,
                GroupId = key.GroupId,
                Status = ChannelReliabilityStatus.Quarantined,
                Verdict = hard.Verdict,
                ProbedModels = models,
                ModelResults = modelResults,
                CheckedAt = now,
                GroupChanged = false,
                Quarantine = quarantine
            };
        }

        return new ChannelReliabilityResult
        {
            KeyId = key.Id,
            GroupId = key.GroupId,
            Status = ChannelReliabilityRules.ResolveStatus(modelResults),
            Verdict = modelResults
                .Select(result => result.Verdict)
                .FirstOrDefault(verdict => verdict != DetectorVerdict.EvidenceInsufficient),
            ProbedModels = models,
            ModelResults = modelResults,
            CheckedAt = now,
            GroupChanged = false,
            Quarantine = snapshot.Records.FirstOrDefault(record =>
                record.GroupId == key.GroupId.Value && record.IsActiveAt(now))
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
            .ToArray();
        var groupNames = (groups ?? [])
            .GroupBy(group => group.Id)
            .ToDictionary(group => group.Key, group => group.First().Name);
        var activeByGroup = snapshot.Records
            .Where(record => record.IsActiveAt(completedAt))
            .GroupBy(record => record.GroupId)
            .ToDictionary(group => group.Key, group => group.First());
        var groupSummaries = results
            .Where(result => result.GroupId is > 0)
            .GroupBy(result => result.GroupId!.Value)
            .Select(group =>
            {
                var groupResults = group.ToArray();
                var quarantine = activeByGroup.TryGetValue(group.Key, out var active)
                    ? active
                    : groupResults.Select(result => result.Quarantine).FirstOrDefault(record => record is not null);
                return new ChannelReliabilityGroupSummary
                {
                    GroupId = group.Key,
                    GroupName = groupNames.TryGetValue(group.Key, out var name) ? name : string.Empty,
                    Status = quarantine is not null
                        ? ChannelReliabilityStatus.Quarantined
                        : ChannelReliabilityRules.ResolveStatus(
                            groupResults.SelectMany(result => result.ModelResults).ToArray()),
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
                var quarantine = result?.Quarantine ??
                    (key.GroupId is > 0 && activeByGroup.TryGetValue(key.GroupId.Value, out var active) ? active : null);
                return new ChannelReliabilityKeySummary
                {
                    KeyId = key.Id,
                    KeyName = key.Name,
                    GroupId = key.GroupId,
                    HasDetectorBinding = binding is { Enabled: true },
                    HasDetectorCredential = detectorApiKeys.TryGetValue(key.Id, out var secret) &&
                        !string.IsNullOrWhiteSpace(secret),
                    Status = result?.Status ?? ChannelReliabilityStatus.EvidenceInsufficient,
                    Verdict = result?.Verdict,
                    Models = result?.ProbedModels ?? [],
                    LastCheckedAt = result?.CheckedAt,
                    NextCheckAt = result?.CheckedAt is { } checkedAt
                        ? checkedAt + DetectionInterval()
                        : null,
                    QuarantinedUntil = quarantine?.ExpiresAt
                };
            })
            .ToArray();

        return new ChannelReliabilityCycleResult
        {
            Enabled = enabled,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            Results = results,
            Keys = keySummaries,
            Groups = groupSummaries,
            Quarantine = snapshot
        };
    }

    private TimeSpan DetectionInterval() => TimeSpan.FromSeconds(
        Math.Clamp(_settings.ReliabilityDetectionIntervalSeconds, 60, 86_400));

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
