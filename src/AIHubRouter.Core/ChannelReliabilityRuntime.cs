namespace AIHubRouter.Core;

/// <summary>
/// Keeps the current reliability run and a bounded, secret-free audit timeline.
/// The monitor owns the single writer; readers receive immutable snapshots.
/// </summary>
public sealed class ChannelReliabilityRuntime
{
    private const int MaxEvents = 1024;
    private const int MaxProbes = 512;
    private readonly object _gate = new();
    private readonly Dictionary<string, ChannelReliabilityProbeProgress> _probes = [];
    private readonly List<ChannelReliabilityAuditEvent> _events = [];
    private ChannelReliabilityRuntimeSnapshot _snapshot = new();
    private long _sequence;

    public ChannelReliabilityRuntimeSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot with
                {
                    Probes = _probes.Values
                        .OrderBy(probe => probe.KeyId)
                        .ThenBy(probe => probe.Model, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(probe => probe.Family)
                        .ToArray(),
                    Events = _events.ToArray()
                };
            }
        }
    }

    public string BeginRun(
        ChannelReliabilityTrigger trigger,
        DateTimeOffset startedAt,
        int selectedKeyCount)
    {
        lock (_gate)
        {
            var runId = Guid.NewGuid().ToString("N");
            _probes.Clear();
            var timelineTruncated = _snapshot.TimelineTruncated;
            _snapshot = new ChannelReliabilityRuntimeSnapshot
            {
                Enabled = true,
                Phase = ChannelReliabilityRunPhase.Running,
                RunId = runId,
                Trigger = trigger,
                StartedAt = startedAt,
                SelectedKeyCount = selectedKeyCount,
                LastEventSequence = _sequence,
                TimelineTruncated = timelineTruncated
            };
            AppendEvent(runId, startedAt, ChannelReliabilityEventType.RunStarted, null, null,
                ChannelReliabilityProbeFamily.Process, ChannelReliabilityProbeStage.Running,
                null, null, null, null);
            return runId;
        }
    }

    public void SetNextCheckAt(DateTimeOffset? nextCheckAt)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with { NextCheckAt = nextCheckAt };
        }
    }

    public void QueueModel(ApiKeyInfo key, string model, DateTimeOffset occurredAt)
    {
        lock (_gate)
        {
            var runId = _snapshot.RunId;
            if (runId is null)
            {
                return;
            }

            UpsertProbe(key, model, ChannelReliabilityProbeFamily.Process,
                ChannelReliabilityProbeStage.Queued, occurredAt, null, null, null, null, null);
            AppendEvent(runId, occurredAt, ChannelReliabilityEventType.ProbeQueued, key, model,
                ChannelReliabilityProbeFamily.Process, ChannelReliabilityProbeStage.Queued,
                null, null, null, null);
            _snapshot = _snapshot with { TotalProbeCount = _snapshot.TotalProbeCount + 1 };
        }
    }

    public void StartModel(ApiKeyInfo key, string model, DateTimeOffset occurredAt)
    {
        lock (_gate)
        {
            if (_snapshot.RunId is null)
            {
                return;
            }

            UpsertProbe(key, model, ChannelReliabilityProbeFamily.Process,
                ChannelReliabilityProbeStage.Running, occurredAt, occurredAt, null, null, null, null);
            AppendEvent(_snapshot.RunId, occurredAt, ChannelReliabilityEventType.ProbeStarted, key, model,
                ChannelReliabilityProbeFamily.Process, ChannelReliabilityProbeStage.Running,
                null, null, null, null);
        }
    }

    public void CompleteModel(ApiKeyInfo key, DetectorResult result, DateTimeOffset occurredAt)
    {
        lock (_gate)
        {
            if (_snapshot.RunId is null)
            {
                return;
            }

            var processStage = result.ErrorCategory == DetectorErrorCategory.None
                ? ChannelReliabilityProbeStage.Completed
                : ChannelReliabilityProbeStage.Failed;
            var status = result.Status;
            UpsertProbe(key, result.Model, ChannelReliabilityProbeFamily.Process, processStage,
                occurredAt, result.CheckedAt, result, result.NetworkSummary, result.EvidenceSummary, null);
            AppendEvent(_snapshot.RunId, occurredAt,
                processStage == ChannelReliabilityProbeStage.Completed
                    ? ChannelReliabilityEventType.ProbeCompleted
                    : ChannelReliabilityEventType.ProbeFailed,
                key, result.Model, ChannelReliabilityProbeFamily.Process, processStage, status,
                result, null, null);

            foreach (var family in new[]
                     {
                         ChannelReliabilityProbeFamily.Network,
                         ChannelReliabilityProbeFamily.Juice,
                         ChannelReliabilityProbeFamily.Identity,
                         ChannelReliabilityProbeFamily.Coverage,
                         ChannelReliabilityProbeFamily.Probability,
                         ChannelReliabilityProbeFamily.Verdict
                     })
            {
                var stage = ResolveFamilyStage(result, family);
                UpsertProbe(key, result.Model, family, stage, occurredAt, result.CheckedAt,
                    result, result.NetworkSummary, result.EvidenceSummary, null);
                AppendEvent(_snapshot.RunId, occurredAt,
                    stage switch
                    {
                        ChannelReliabilityProbeStage.Completed => ChannelReliabilityEventType.ProbeCompleted,
                        ChannelReliabilityProbeStage.Skipped => ChannelReliabilityEventType.ProbeSkipped,
                        _ => ChannelReliabilityEventType.ProbeFailed
                    },
                    key, result.Model, family, stage, status, result, null, null);
            }

            _snapshot = _snapshot with
            {
                CompletedProbeCount = _snapshot.CompletedProbeCount + 1,
                FailedProbeCount = _snapshot.FailedProbeCount +
                    (result.ErrorCategory == DetectorErrorCategory.None ? 0 : 1)
            };
        }
    }

    public void SkipKey(ApiKeyInfo key, ChannelReliabilityStatus status, DateTimeOffset occurredAt)
    {
        lock (_gate)
        {
            if (_snapshot.RunId is null)
            {
                return;
            }

            UpsertProbe(key, string.Empty, ChannelReliabilityProbeFamily.Process,
                ChannelReliabilityProbeStage.Skipped, occurredAt, null, null, null, null, status);
            AppendEvent(_snapshot.RunId, occurredAt, ChannelReliabilityEventType.ProbeSkipped,
                key, null, ChannelReliabilityProbeFamily.Process, ChannelReliabilityProbeStage.Skipped,
                status, null, null, null);
        }
    }

    public void CompleteRun(
        ChannelReliabilityCycleResult result,
        DateTimeOffset completedAt)
    {
        lock (_gate)
        {
            if (_snapshot.RunId is null)
            {
                return;
            }

            var warning = _snapshot.SelectedKeyCount == 0 ||
                _snapshot.TotalProbeCount == 0 ||
                result.Keys.Any(key => key.Status is
                ChannelReliabilityStatus.Unconfigured or
                ChannelReliabilityStatus.Unavailable or
                ChannelReliabilityStatus.EvidenceInsufficient);
            var phase = warning
                ? ChannelReliabilityRunPhase.CompletedWithWarnings
                : ChannelReliabilityRunPhase.Completed;
            AppendEvent(_snapshot.RunId, completedAt, ChannelReliabilityEventType.RunCompleted,
                null, null, ChannelReliabilityProbeFamily.Verdict,
                ChannelReliabilityProbeStage.Completed, null, null, null, null);
            _snapshot = _snapshot with
            {
                Phase = phase,
                CompletedAt = completedAt,
                LastEventSequence = _sequence,
                TimelineTruncated = _snapshot.TimelineTruncated || _events.Count >= MaxEvents
            };
        }
    }

    public void RecordQuarantine(
        ApiKeyInfo key,
        ChannelQuarantineRecord record,
        bool applied,
        DateTimeOffset occurredAt)
    {
        lock (_gate)
        {
            if (_snapshot.RunId is null)
            {
                return;
            }

            AppendEvent(_snapshot.RunId, occurredAt,
                applied
                    ? ChannelReliabilityEventType.QuarantineApplied
                    : ChannelReliabilityEventType.QuarantineRejected,
                key, record.SourceModel, ChannelReliabilityProbeFamily.Verdict,
                ChannelReliabilityProbeStage.Completed,
                applied ? ChannelReliabilityStatus.Quarantined : ChannelReliabilityStatus.EvidenceInsufficient,
                null, record.ExpiresAt, null);
        }
    }

    public void Abort(ChannelReliabilityRunPhase phase, DateTimeOffset occurredAt)
    {
        lock (_gate)
        {
            if (_snapshot.RunId is null)
            {
                return;
            }

            if (phase == ChannelReliabilityRunPhase.Cancelled)
            {
                foreach (var pair in _probes
                             .Where(pair => pair.Value.Stage is
                                 ChannelReliabilityProbeStage.Queued or ChannelReliabilityProbeStage.Running)
                             .ToArray())
                {
                    var cancelledProbe = pair.Value with
                    {
                        Stage = ChannelReliabilityProbeStage.Cancelled,
                        CompletedAt = occurredAt,
                        DurationMs = pair.Value.StartedAt is { } started
                            ? Math.Max(0L, (long)Math.Round((occurredAt - started).TotalMilliseconds))
                            : null,
                        ErrorCategory = DetectorErrorCategory.Cancelled
                    };
                    _probes[pair.Key] = cancelledProbe;
                    AppendEvent(_snapshot.RunId, occurredAt,
                        ChannelReliabilityEventType.ProbeCancelled,
                        new ApiKeyInfo
                        {
                            Id = cancelledProbe.KeyId,
                            Name = cancelledProbe.KeyName,
                            GroupId = cancelledProbe.GroupId
                        },
                        cancelledProbe.Model,
                        cancelledProbe.Family,
                        ChannelReliabilityProbeStage.Cancelled,
                        cancelledProbe.Status,
                        null,
                        null,
                        DetectorErrorCategory.Cancelled);
                }
            }

            var eventType = phase == ChannelReliabilityRunPhase.Cancelled
                ? ChannelReliabilityEventType.RunCancelled
                : ChannelReliabilityEventType.RunFailed;
            AppendEvent(_snapshot.RunId, occurredAt, eventType, null, null,
                ChannelReliabilityProbeFamily.Verdict,
                phase == ChannelReliabilityRunPhase.Cancelled
                    ? ChannelReliabilityProbeStage.Cancelled
                    : ChannelReliabilityProbeStage.Failed,
                null, null, null, null);
            _snapshot = _snapshot with
            {
                Phase = phase,
                CompletedAt = occurredAt,
                LastEventSequence = _sequence,
                TimelineTruncated = _snapshot.TimelineTruncated || _events.Count >= MaxEvents
            };
        }
    }

    public void SetDisabled(DateTimeOffset occurredAt, DateTimeOffset? nextCheckAt = null)
    {
        lock (_gate)
        {
            _snapshot = new ChannelReliabilityRuntimeSnapshot
            {
                Enabled = false,
                Phase = ChannelReliabilityRunPhase.Disabled,
                CompletedAt = occurredAt,
                NextCheckAt = nextCheckAt,
                LastEventSequence = _sequence,
                TimelineTruncated = _snapshot.TimelineTruncated
            };
        }
    }

    private void UpsertProbe(
        ApiKeyInfo key,
        string model,
        ChannelReliabilityProbeFamily family,
        ChannelReliabilityProbeStage stage,
        DateTimeOffset occurredAt,
        DateTimeOffset? startedAt,
        DetectorResult? result,
        DetectorNetworkSummary? network,
        DetectorEvidenceSummary? evidence,
        ChannelReliabilityStatus? status)
    {
        var probeKey = $"{key.Id}:{model}:{family}";
        _probes[probeKey] = new ChannelReliabilityProbeProgress
        {
            KeyId = key.Id,
            KeyName = key.Name,
            GroupId = key.GroupId,
            Model = model,
            Family = family,
            Stage = stage,
            QueuedAt = _probes.TryGetValue(probeKey, out var previous) ? previous.QueuedAt : occurredAt,
            StartedAt = startedAt ?? previous?.StartedAt,
            CompletedAt = stage is ChannelReliabilityProbeStage.Completed or
                ChannelReliabilityProbeStage.Failed or
                ChannelReliabilityProbeStage.Cancelled or
                ChannelReliabilityProbeStage.Skipped ? occurredAt : null,
            DurationMs = startedAt is { } started
                ? Math.Max(0L, (long)Math.Round((occurredAt - started).TotalMilliseconds))
                : null,
            Status = status ?? result?.Status,
            Verdict = result?.Verdict,
            ErrorCategory = result?.ErrorCategory ?? DetectorErrorCategory.None,
            Network = network,
            Evidence = evidence,
            QuarantinedUntil = null
        };
        if (_probes.Count > MaxProbes)
        {
            // ha-min: bounded in-memory probe map; replace with an indexed ring only if the probe cap grows.
            var oldest = _probes.Keys.First();
            _probes.Remove(oldest);
            _snapshot = _snapshot with { TimelineTruncated = true };
        }
    }

    private static ChannelReliabilityProbeStage ResolveFamilyStage(
        DetectorResult result,
        ChannelReliabilityProbeFamily family)
    {
        if (result.ErrorCategory != DetectorErrorCategory.None)
        {
            return ChannelReliabilityProbeStage.Failed;
        }

        var evidence = result.EvidenceSummary;
        return family switch
        {
            ChannelReliabilityProbeFamily.Probability when evidence?.ProbabilityEnabled != true =>
                ChannelReliabilityProbeStage.Skipped,
            ChannelReliabilityProbeFamily.Network when result.NetworkSummary is null =>
                ChannelReliabilityProbeStage.Skipped,
            ChannelReliabilityProbeFamily.Juice or
                ChannelReliabilityProbeFamily.Identity or
                ChannelReliabilityProbeFamily.Coverage or
                ChannelReliabilityProbeFamily.Verdict when evidence is null =>
                ChannelReliabilityProbeStage.Skipped,
            _ => ChannelReliabilityProbeStage.Completed
        };
    }

    private void AppendEvent(
        string? runId,
        DateTimeOffset occurredAt,
        ChannelReliabilityEventType eventType,
        ApiKeyInfo? key,
        string? model,
        ChannelReliabilityProbeFamily family,
        ChannelReliabilityProbeStage stage,
        ChannelReliabilityStatus? status,
        DetectorResult? result,
        DateTimeOffset? quarantinedUntil,
        DetectorErrorCategory? errorCategory)
    {
        _sequence++;
        _events.Add(new ChannelReliabilityAuditEvent
        {
            Sequence = _sequence,
            RunId = runId,
            OccurredAt = occurredAt,
            EventType = eventType,
            Trigger = _snapshot.Trigger ?? ChannelReliabilityTrigger.Scheduled,
            KeyId = key?.Id,
            KeyName = key?.Name,
            GroupId = key?.GroupId,
            Model = model,
            Family = family,
            Stage = stage,
            DurationMs = null,
            Status = status,
            Verdict = result?.Verdict,
            ErrorCategory = errorCategory ?? result?.ErrorCategory ?? DetectorErrorCategory.None,
            QuarantinedUntil = quarantinedUntil
        });
        if (_events.Count > MaxEvents)
        {
            // ha-min: bounded timeline with a small fixed cap; use a ring buffer if long-lived audit volume grows.
            _events.RemoveAt(0);
            _snapshot = _snapshot with { TimelineTruncated = true };
        }

        _snapshot = _snapshot with { LastEventSequence = _sequence };
    }
}
