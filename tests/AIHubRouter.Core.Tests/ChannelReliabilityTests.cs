using System.Text.Json;
using AIHubRouter.Core;

namespace AIHubRouter.Core.Tests;

/// <summary>
/// Focused, dependency-free checks for detector model selection and local
/// channel quarantine persistence.
/// </summary>
public static class ChannelReliabilityTests
{
    public static void TestDetectorModelNamesAndProbeSelection()
    {
        Assert(
            DetectorModelNames.Models.SequenceEqual(
                [DetectorModelNames.Sol, DetectorModelNames.Terra, DetectorModelNames.Luna]),
            "The fixed detector model set changed unexpectedly.");
        Assert(DetectorModelNames.Normalize(" SOL ") == DetectorModelNames.Sol, "Model normalization failed.");
        Assert(DetectorModelNames.IsSupported("terra"), "Terra should be a supported detector model.");

        var selected = ChannelReliabilityRules.SelectProbeModels(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [DetectorModelNames.Sol] = "healthy",
                [DetectorModelNames.Terra] = "healthy",
                [DetectorModelNames.Luna] = "healthy"
            },
            new DetectorBinding
            {
                Models = [DetectorModelNames.Sol, DetectorModelNames.Terra]
            });

        Assert(
            selected.SequenceEqual([DetectorModelNames.Sol, DetectorModelNames.Terra]),
            "A sol/terra-only binding must never probe luna.");
    }

    public static void TestSelectProbeModelsSkipsFailedAndUnknown()
    {
        var selected = ChannelReliabilityRules.SelectProbeModels(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [DetectorModelNames.Sol] = "failed",
                [DetectorModelNames.Terra] = "unknown",
                [DetectorModelNames.Luna] = "failed"
            },
            new DetectorBinding
            {
                Models = [DetectorModelNames.Sol, DetectorModelNames.Terra, DetectorModelNames.Luna]
            });

        Assert(selected.Count == 0, "Failed and unknown detector models must not be probed.");

        var disabled = ChannelReliabilityRules.SelectProbeModels(
            new Dictionary<string, string> { [DetectorModelNames.Sol] = "healthy" },
            new DetectorBinding { Models = [DetectorModelNames.Sol], Enabled = false });
        Assert(disabled.Count == 0, "Disabled detector bindings must not be probed.");
    }

    public static void TestHardVerdictClassification()
    {
        var hardVerdicts = new[]
        {
            DetectorVerdict.PossibleNonGpt,
            DetectorVerdict.JuiceMixed,
            DetectorVerdict.ProbabilityOnlyMixed
        };
        var softVerdicts = new[]
        {
            DetectorVerdict.EvidenceInsufficient,
            DetectorVerdict.Passed
        };

        foreach (var verdict in hardVerdicts)
        {
            Assert(ChannelReliabilityRules.IsHardVerdict(verdict), $"{verdict} must be hard.");
            Assert(
                new DetectorResult
                {
                    Model = DetectorModelNames.Sol,
                    ClaimedModel = "gpt-5.6-sol",
                    Verdict = verdict,
                    Official = true
                }.IsQuarantineEligible,
                $"{verdict} with official matching evidence must be quarantine eligible.");
            Assert(
                !new DetectorResult
                {
                    Model = DetectorModelNames.Sol,
                    ClaimedModel = "gpt-5.6-sol",
                    Verdict = verdict,
                    Official = true,
                    ErrorCategory = DetectorErrorCategory.Timeout
                }.IsQuarantineEligible,
                $"{verdict} with a timeout must not be quarantine eligible.");
            Assert(
                !new DetectorResult
                {
                    Model = DetectorModelNames.Sol,
                    ClaimedModel = "gpt-5.6-terra",
                    Verdict = verdict,
                    Official = true
                }.IsQuarantineEligible,
                $"{verdict} with a mismatched claimed model must not be quarantine eligible.");
        }

        foreach (var verdict in softVerdicts)
        {
            Assert(!ChannelReliabilityRules.IsHardVerdict(verdict), $"{verdict} must not be hard.");
        }

        var status = ChannelReliabilityRules.ResolveStatus(
        [
            new DetectorResult
            {
                Model = DetectorModelNames.Sol,
                ClaimedModel = "gpt-5.6-sol",
                Status = ChannelReliabilityStatus.Passed,
                Verdict = DetectorVerdict.JuiceMixed,
                Official = true
            }
        ]);
        Assert(status == ChannelReliabilityStatus.Quarantined, "A hard verdict must quarantine the channel.");

        var mixedStatus = ChannelReliabilityRules.ResolveStatus(
        [
            new DetectorResult
            {
                Model = DetectorModelNames.Sol,
                ClaimedModel = "gpt-5.6-sol",
                Status = ChannelReliabilityStatus.EvidenceInsufficient,
                Verdict = DetectorVerdict.JuiceMixed,
                Official = true
            },
            new DetectorResult
            {
                Model = DetectorModelNames.Terra,
                Status = ChannelReliabilityStatus.Unavailable,
                Verdict = DetectorVerdict.EvidenceInsufficient,
                ErrorCategory = DetectorErrorCategory.Timeout
            }
        ]);
        Assert(mixedStatus == ChannelReliabilityStatus.Unavailable,
            "An execution error must take precedence over a hard verdict from another model.");
    }

    public static void TestMapperValidatesCompleteEvidence()
    {
        const string valid = """
            {"status":"complete","overall_verdict":"Juice混用","error_code":null,"official":true,"claimed_model":"gpt-5.6-sol","network_summary":{"logical_tasks":4,"logical_completed":4,"successful":4,"final_errors":0,"cancelled":0,"http_attempts":4,"retries":0,"in_flight":0},"evidence_summary":{"verdict_available":true,"hard_verdict":true,"juice_state":"juice_mixed","probability_enabled":false,"probability_formal_eligible":null,"evidence_insufficient":false}}
            """;
        var accepted = MapWorkerSummary(valid);
        Assert(accepted.ErrorCategory == DetectorErrorCategory.None && accepted.IsQuarantineEligible,
            "A complete, internally consistent official hard verdict must remain quarantine eligible.");

        string[] invalid =
        [
            valid.Replace("\"final_errors\":0", "\"final_errors\":1", StringComparison.Ordinal),
            valid.Replace("\"logical_completed\":4", "\"logical_completed\":3", StringComparison.Ordinal),
            valid.Replace("\"hard_verdict\":true", "\"hard_verdict\":false", StringComparison.Ordinal),
            valid.Replace("\"juice_state\":\"juice_mixed\"", "\"juice_state\":\"juice_pass\"", StringComparison.Ordinal)
        ];
        foreach (var summary in invalid)
        {
            var rejected = MapWorkerSummary(summary);
            Assert(rejected.ErrorCategory == DetectorErrorCategory.InvalidResponse,
                "A conflicting complete summary must be rejected as an invalid response.");
            Assert(!rejected.IsQuarantineEligible,
                "A conflicting complete summary must never become quarantine eligible.");
        }
    }

    public static void TestQuarantineExpiresAfterTwentyFourHours()
    {
        var quarantinedAt = new DateTimeOffset(2026, 8, 10, 3, 0, 0, TimeSpan.Zero);
        var record = new ChannelQuarantineRecord
        {
            GroupId = 42,
            QuarantinedAt = quarantinedAt,
            ExpiresAt = quarantinedAt + JsonChannelQuarantineStore.DefaultIsolationDuration,
            Verdict = DetectorVerdict.JuiceMixed,
            SourceKeyId = 7,
            SourceModel = DetectorModelNames.Sol
        };

        Assert(
            record.ExpiresAt == quarantinedAt.AddHours(24),
            "The default quarantine duration must be exactly 24 hours.");
        Assert(record.IsActiveAt(record.ExpiresAt.AddTicks(-1)), "A quarantine should be active before expiry.");
        Assert(!record.IsActiveAt(record.ExpiresAt), "A quarantine must expire at its expiry instant.");

        var snapshot = new ChannelQuarantineSnapshot { Records = [record] };
        Assert(snapshot.IsActive(42, quarantinedAt.AddHours(23)), "Snapshot active filtering lost the group.");
        Assert(
            snapshot.GetActiveGroupIds(quarantinedAt.AddHours(24)).Count == 0,
            "Expired quarantine records must be excluded from active groups.");
    }

    public static void TestJsonQuarantineStoreRoundtripAndActiveFiltering()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AIHubRouter.Tests", Guid.NewGuid().ToString("N"));
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var initial = new ChannelQuarantineRecord
        {
            GroupId = 100,
            QuarantinedAt = now.AddHours(-1),
            ExpiresAt = now.AddHours(23),
            Verdict = DetectorVerdict.JuiceMixed,
            SourceKeyId = 10,
            SourceModel = DetectorModelNames.Terra
        };
        var replacement = initial with
        {
            QuarantinedAt = now.AddHours(-25),
            ExpiresAt = now.AddHours(-1),
            Verdict = DetectorVerdict.JuiceMixed
        };
        var activeOtherGroup = new ChannelQuarantineRecord
        {
            GroupId = 200,
            QuarantinedAt = now.AddMinutes(-5),
            ExpiresAt = now.AddHours(24),
            Verdict = DetectorVerdict.ProbabilityOnlyMixed,
            SourceKeyId = 20,
            SourceModel = DetectorModelNames.Sol
        };

        try
        {
            var store = new JsonChannelQuarantineStore(directory);
            store.Save(initial);
            store.Save(replacement);
            store.Save(activeOtherGroup);

            var reloaded = new JsonChannelQuarantineStore(directory);
            var history = reloaded.LoadHistory();
            var latest = reloaded.LoadLatest();
            var active = reloaded.GetActive(now);

            Assert(history.Count == 3, "Quarantine history did not round-trip all decisions.");
            Assert(latest.Count == 2, "Latest quarantine state did not collapse by group.");
            Assert(
                latest.Single(record => record.GroupId == 100).Verdict == DetectorVerdict.JuiceMixed,
                "The latest decision for a group was not retained.");
            Assert(
                active.Count == 1 && active[0].GroupId == activeOtherGroup.GroupId,
                "Active filtering must exclude expired latest decisions.");
            Assert(File.Exists(reloaded.StoragePath), "The quarantine JSON file was not written.");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    public static void TestReliabilitySerializationExcludesSecrets()
    {
        var json = JsonSerializer.Serialize(new ChannelReliabilityCycleResult
        {
            Runtime = new ChannelReliabilityRuntimeSnapshot
            {
                Enabled = true,
                Phase = ChannelReliabilityRunPhase.Running,
                RunId = "run-safe-id",
                Trigger = ChannelReliabilityTrigger.Scheduled,
                TotalProbeCount = 2,
                CompletedProbeCount = 1,
                Probes =
                [
                    new ChannelReliabilityProbeProgress
                    {
                        KeyId = 11,
                        KeyName = "safe-key",
                        GroupId = 22,
                        Model = DetectorModelNames.Sol,
                        Family = ChannelReliabilityProbeFamily.Juice,
                        Stage = ChannelReliabilityProbeStage.Completed,
                        Network = new DetectorNetworkSummary
                        {
                            HttpAttempts = 2,
                            Retries = 1,
                            ErrorCategories =
                            [new DetectorErrorCount { Category = "timeout", Count = 1 }]
                        },
                        Evidence = new DetectorEvidenceSummary
                        {
                            JuiceState = "juice_pass",
                            JuiceValidCompleted = 8,
                            VerdictAvailable = true
                        }
                    }
                ],
                Events =
                [
                    new ChannelReliabilityAuditEvent
                    {
                        Sequence = 1,
                        RunId = "run-safe-id",
                        EventType = ChannelReliabilityEventType.ProbeCompleted,
                        OccurredAt = new DateTimeOffset(2026, 8, 10, 0, 1, 0, TimeSpan.Zero),
                        KeyId = 11,
                        GroupId = 22,
                        Model = DetectorModelNames.Sol,
                        Family = ChannelReliabilityProbeFamily.Juice,
                        Stage = ChannelReliabilityProbeStage.Completed
                    }
                ]
            },
            Results =
            [
                new ChannelReliabilityResult
                {
                    KeyId = 11,
                    GroupId = 22,
                    Status = ChannelReliabilityStatus.Passed,
                    Verdict = DetectorVerdict.Passed,
                    ProbedModels = [DetectorModelNames.Sol]
                }
            ],
            Quarantine = new ChannelQuarantineSnapshot
            {
                CapturedAt = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero),
                Records =
                [
                    new ChannelQuarantineRecord
                    {
                        GroupId = 22,
                        QuarantinedAt = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero),
                        ExpiresAt = new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero),
                        Verdict = DetectorVerdict.JuiceMixed,
                        SourceKeyId = 11,
                        SourceModel = DetectorModelNames.Sol
                    }
                ]
            }
        });

        using var document = JsonDocument.Parse(json);
        var propertyNames = document.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        Assert(propertyNames.Contains("results"), "Reliability results were not serialized.");
        Assert(propertyNames.Contains("quarantine"), "Quarantine state was not serialized.");
        Assert(propertyNames.Contains("runtime"), "Reliability runtime state was not serialized.");
        Assert(json.Contains("httpAttempts", StringComparison.Ordinal), "Safe network metrics were not serialized.");
        Assert(json.Contains("juiceValidCompleted", StringComparison.Ordinal), "Safe evidence metrics were not serialized.");

        foreach (var forbidden in new[]
                 {
                     "secret", "token", "password", "cookie", "credential", "bearerToken",
                     "apiKey", "prompt", "requestBody", "responseBody", "authorization", "stderr", "traceback"
                 })
        {
            Assert(
                !json.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"Serialized reliability state contains a forbidden secret field: {forbidden}.");
        }
        Assert(!json.Contains("isHardAnomaly", StringComparison.Ordinal), "Computed properties must not be serialized.");
        Assert(!json.Contains("isQuarantineEligible", StringComparison.Ordinal), "Computed properties must not be serialized.");
    }

    public static void TestMonitorKeepsKeyBindingsIndependent()
    {
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var keys = new[]
        {
            new ApiKeyInfo { Id = 1, Name = "sol-terra-key", GroupId = 10, Status = "active" },
            new ApiKeyInfo { Id = 2, Name = "luna-key", GroupId = 20, Status = "active" }
        };
        var settings = new PersistentAppSettings
        {
            ReliabilityDetectionEnabled = true,
            DetectorBindings =
            [
                new DetectorBinding
                {
                    KeyId = 1,
                    BaseUrl = "https://channel.example.test/v1",
                    Models = [DetectorModelNames.Sol, DetectorModelNames.Terra]
                },
                new DetectorBinding
                {
                    KeyId = 2,
                    BaseUrl = "https://luna.example.test/v1",
                    Models = [DetectorModelNames.Luna]
                }
            ]
        };
        var credentials = new PersistentCredentials
        {
            DetectorApiKeys = new Dictionary<long, string> { [1] = "key-one" }
        };
        var detector = new RecordingDetector(DetectorVerdict.Passed);
        var store = new MemoryQuarantineStore();
        using var monitor = new ChannelReliabilityMonitor(
            settings,
            credentials,
            detector,
            store,
            () => now);

        var result = monitor.CheckAsync(
                keys,
                new Dictionary<long, IReadOnlyDictionary<string, string>>
                {
                    [10] = new Dictionary<string, string>
                    {
                        [DetectorModelNames.Sol] = "healthy",
                        [DetectorModelNames.Terra] = "healthy",
                        [DetectorModelNames.Luna] = "healthy"
                    },
                    [20] = new Dictionary<string, string>
                    {
                        [DetectorModelNames.Luna] = "healthy"
                    }
                },
                currentKeyResolver: keyId => keys.FirstOrDefault(key => key.Id == keyId))
            .GetAwaiter()
            .GetResult();

        Assert(
            detector.Calls.SequenceEqual([(1L, DetectorModelNames.Sol), (1L, DetectorModelNames.Terra)]),
            "The monitor must probe only healthy models declared by each Key binding.");
        Assert(
            result.Keys.Single(key => key.KeyId == 2).Status == ChannelReliabilityStatus.Unconfigured,
            "A missing detector credential must not borrow another Key's credential.");
        Assert(result.ExcludedGroupIds.Count == 0, "Passed checks must not exclude a group.");
        Assert(result.Runtime?.Phase == ChannelReliabilityRunPhase.CompletedWithWarnings,
            "An unconfigured selected Key must keep the runtime in a warning completion phase.");
        Assert(result.Runtime?.Probes.Any(probe =>
                probe.KeyId == 1 &&
                probe.Model == DetectorModelNames.Sol &&
                probe.Family == ChannelReliabilityProbeFamily.Network &&
                probe.Stage == ChannelReliabilityProbeStage.Completed) == true,
            "The runtime snapshot must expose categorized per-model probe progress.");
    }

    public static void TestMonitorQuarantinesHardVerdict()
    {
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var key = new ApiKeyInfo { Id = 9, Name = "mixed-key", GroupId = 90, Status = "active" };
        var settings = new PersistentAppSettings
        {
            ReliabilityDetectionEnabled = true,
            DetectorBindings =
            [new DetectorBinding
            {
                KeyId = key.Id,
                BaseUrl = "https://channel.example.test/v1",
                Models = [DetectorModelNames.Sol]
            }],
            ReliabilityQuarantineHours = 24
        };
        var credentials = new PersistentCredentials
        {
            DetectorApiKeys = new Dictionary<long, string> { [key.Id] = "key-nine" }
        };
        var store = new MemoryQuarantineStore();
        using var monitor = new ChannelReliabilityMonitor(
            settings,
            credentials,
            new RecordingDetector(DetectorVerdict.PossibleNonGpt),
            store,
            () => now);

        var result = monitor.CheckAsync(
                [key],
                new Dictionary<long, IReadOnlyDictionary<string, string>>
                {
                    [90] = new Dictionary<string, string>
                    {
                        [DetectorModelNames.Sol] = "healthy"
                    }
                })
            .GetAwaiter()
            .GetResult();

        Assert(result.ExcludedGroupIds.SequenceEqual([90L]), "A hard detector verdict must exclude its group.");
        Assert(store.Saved.Count == 1, "A new hard verdict must be persisted once.");
        Assert(store.Saved[0].ExpiresAt == now.AddHours(24), "Quarantine must last 24 hours.");
        Assert(result.Runtime?.Events.Any(item =>
                item.EventType == ChannelReliabilityEventType.QuarantineApplied &&
                item.QuarantinedUntil == now.AddHours(24)) == true,
            "The runtime timeline must expose the applied quarantine decision.");
    }

    public static void TestMonitorDoesNotQuarantineMixedExecutionErrors()
    {
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var key = new ApiKeyInfo { Id = 12, Name = "mixed-result-key", GroupId = 120, Status = "active" };
        var settings = new PersistentAppSettings
        {
            ReliabilityDetectionEnabled = true,
            DetectorBindings =
            [new DetectorBinding
            {
                KeyId = key.Id,
                BaseUrl = "https://channel.example.test/v1",
                Models = [DetectorModelNames.Sol, DetectorModelNames.Terra]
            }]
        };
        var credentials = new PersistentCredentials
        {
            DetectorApiKeys = new Dictionary<long, string> { [key.Id] = "key-twelve" }
        };
        using var monitor = new ChannelReliabilityMonitor(
            settings,
            credentials,
            new SequenceDetector(
            [
                (DetectorVerdict.PossibleNonGpt, DetectorErrorCategory.None),
                (DetectorVerdict.EvidenceInsufficient, DetectorErrorCategory.Timeout)
            ]),
            new MemoryQuarantineStore(),
            () => now);

        var result = monitor.CheckAsync(
                [key],
                new Dictionary<long, IReadOnlyDictionary<string, string>>
                {
                    [120] = new Dictionary<string, string>
                    {
                        [DetectorModelNames.Sol] = "healthy",
                        [DetectorModelNames.Terra] = "healthy"
                    }
                })
            .GetAwaiter()
            .GetResult();

        Assert(result.ExcludedGroupIds.Count == 0,
            "A hard verdict mixed with an execution timeout must not quarantine the group.");
        Assert(result.Results.Single().Status == ChannelReliabilityStatus.Unavailable,
            "A mixed hard verdict and timeout must surface as unavailable.");
    }

    public static void TestMonitorDryRunReportsWouldQuarantineWithoutApplyingIt()
    {
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var key = new ApiKeyInfo { Id = 13, Name = "preview-key", GroupId = 130, Status = "active" };
        var store = new MemoryQuarantineStore();
        using var monitor = CreateConfiguredMonitor(
            key,
            new RecordingDetector(DetectorVerdict.JuiceMixed),
            store,
            () => now);

        var result = monitor.CheckAsync(
                [key],
                HealthyModels(130, DetectorModelNames.Sol),
                dryRun: true,
                force: true)
            .GetAwaiter()
            .GetResult();
        var decision = result.Results.Single();

        Assert(decision.WouldQuarantine, "Dry-run hard evidence must expose WouldQuarantine.");
        Assert(decision.Status == ChannelReliabilityStatus.EvidenceInsufficient,
            "Dry-run hard evidence must not claim that quarantine was applied.");
        Assert(decision.Quarantine is null, "Dry-run must not expose a proposed quarantine as active state.");
        Assert(store.Saved.Count == 0, "Dry-run must not persist quarantine state.");
        Assert(result.Runtime?.Events.Any(item =>
                item.EventType == ChannelReliabilityEventType.QuarantineRejected) == true,
            "Dry-run must record that the quarantine decision was not applied.");
    }

    public static void TestMonitorKeepsActiveQuarantineVisibleAfterPassingProbe()
    {
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var key = new ApiKeyInfo { Id = 14, Name = "isolated-key", GroupId = 140, Status = "active" };
        var store = new MemoryQuarantineStore();
        store.Save(new ChannelQuarantineRecord
        {
            GroupId = 140,
            QuarantinedAt = now.AddHours(-1),
            ExpiresAt = now.AddHours(23),
            Verdict = DetectorVerdict.JuiceMixed,
            SourceKeyId = key.Id,
            SourceModel = DetectorModelNames.Sol
        });
        using var monitor = CreateConfiguredMonitor(
            key,
            new RecordingDetector(DetectorVerdict.Passed),
            store,
            () => now);

        var result = monitor.CheckAsync(
                [key],
                HealthyModels(140, DetectorModelNames.Sol),
                force: true)
            .GetAwaiter()
            .GetResult();

        Assert(result.Results.Single().Status == ChannelReliabilityStatus.Quarantined,
            "An active quarantine must remain visible even when the latest probe passes.");
        Assert(result.Keys.Single().Status == ChannelReliabilityStatus.Quarantined,
            "Key summary and detailed result must agree while quarantine is active.");
        Assert(result.ExcludedGroupIds.SequenceEqual([140L]),
            "An active quarantine must continue excluding the group until expiry.");
    }

    public static void TestMonitorNormalizesQuarantineThatExpiresDuringCycle()
    {
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var current = now;
        var key = new ApiKeyInfo { Id = 15, Name = "expiry-key", GroupId = 150, Status = "active" };
        var store = new MemoryQuarantineStore(() => current = now.AddHours(25));
        using var monitor = CreateConfiguredMonitor(
            key,
            new RecordingDetector(DetectorVerdict.JuiceMixed),
            store,
            () => current);

        var result = monitor.CheckAsync(
                [key],
                HealthyModels(150, DetectorModelNames.Sol),
                force: true)
            .GetAwaiter()
            .GetResult();

        Assert(result.ExcludedGroupIds.Count == 0,
            "A quarantine that expired before cycle completion must not remain in the route exclusion list.");
        Assert(result.Results.Single().Status != ChannelReliabilityStatus.Quarantined &&
               result.Results.Single().Quarantine is null,
            "The detailed result must remove an expired quarantine.");
        Assert(result.Keys.Single().Status != ChannelReliabilityStatus.Quarantined &&
               result.Groups.Single().Status != ChannelReliabilityStatus.Quarantined,
            "Key and group summaries must agree that an expired quarantine is inactive.");
    }

    public static void TestRuntimeMarksEmptyAndCancelledRunsHonestly()
    {
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var runtime = new ChannelReliabilityRuntime();
        runtime.BeginRun(ChannelReliabilityTrigger.Scheduled, now, selectedKeyCount: 0);
        runtime.CompleteRun(new ChannelReliabilityCycleResult { Keys = [] }, now.AddSeconds(1));
        Assert(runtime.Snapshot.Phase == ChannelReliabilityRunPhase.CompletedWithWarnings,
            "A run with no selected Key or model probe must not appear fully successful.");

        var key = new ApiKeyInfo { Id = 15, Name = "cancel-key", GroupId = 150, Status = "active" };
        runtime.BeginRun(ChannelReliabilityTrigger.Manual, now.AddMinutes(1), selectedKeyCount: 1);
        runtime.QueueModel(key, DetectorModelNames.Sol, now.AddMinutes(1));
        runtime.StartModel(key, DetectorModelNames.Sol, now.AddMinutes(1).AddSeconds(1));
        runtime.Abort(ChannelReliabilityRunPhase.Cancelled, now.AddMinutes(1).AddSeconds(2));

        Assert(runtime.Snapshot.Probes.Single().Stage == ChannelReliabilityProbeStage.Cancelled,
            "A cancelled run must not leave an in-flight model marked Running.");
        Assert(runtime.Snapshot.Events.Any(item =>
                item.EventType == ChannelReliabilityEventType.ProbeCancelled),
            "A cancelled model must emit a ProbeCancelled audit event.");
    }

    public static void TestMonitorReportsUnconfiguredGroupWithoutModelFlattening()
    {
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var keys = new[]
        {
            new ApiKeyInfo { Id = 31, Name = "configured", GroupId = 310, Status = "active" },
            new ApiKeyInfo { Id = 32, Name = "unconfigured", GroupId = 320, Status = "active" }
        };
        var settings = new PersistentAppSettings
        {
            ReliabilityDetectionEnabled = true,
            DetectorBindings =
            [new DetectorBinding
            {
                KeyId = 31,
                BaseUrl = "https://channel.example.test/v1",
                Models = [DetectorModelNames.Sol]
            }]
        };
        var credentials = new PersistentCredentials
        {
            DetectorApiKeys = new Dictionary<long, string> { [31] = "key-thirty-one" }
        };
        using var monitor = new ChannelReliabilityMonitor(
            settings,
            credentials,
            new RecordingDetector(DetectorVerdict.Passed),
            new MemoryQuarantineStore(),
            () => now);

        var result = monitor.CheckAsync(
                keys,
                new Dictionary<long, IReadOnlyDictionary<string, string>>
                {
                    [310] = new Dictionary<string, string>
                    {
                        [DetectorModelNames.Sol] = "healthy"
                    }
                })
            .GetAwaiter()
            .GetResult();

        Assert(result.Keys.Single(item => item.KeyId == 32).Status == ChannelReliabilityStatus.Unconfigured,
            "A missing binding or credential must be visible as unconfigured.");
        Assert(result.Groups.Single(item => item.GroupId == 320).Status == ChannelReliabilityStatus.Unconfigured,
            "Group aggregation must preserve an unconfigured Key status.");
        Assert(result.Groups.Single(item => item.GroupId == 310).Status == ChannelReliabilityStatus.Passed,
            "A configured passing Key must remain passed after group aggregation.");
    }

    private static ChannelReliabilityMonitor CreateConfiguredMonitor(
        ApiKeyInfo key,
        IChannelReliabilityDetector detector,
        IChannelQuarantineStore store,
        Func<DateTimeOffset> utcNow) => new(
            new PersistentAppSettings
            {
                ReliabilityDetectionEnabled = true,
                ReliabilityQuarantineHours = 24,
                DetectorBindings =
                [new DetectorBinding
                {
                    KeyId = key.Id,
                    BaseUrl = "https://channel.example.test/v1",
                    Models = [DetectorModelNames.Sol]
                }]
            },
            new PersistentCredentials
            {
                DetectorApiKeys = new Dictionary<long, string> { [key.Id] = "test-key" }
            },
            detector,
            store,
            utcNow);

    private static IReadOnlyDictionary<long, IReadOnlyDictionary<string, string>> HealthyModels(
        long groupId,
        params string[] models) =>
        new Dictionary<long, IReadOnlyDictionary<string, string>>
        {
            [groupId] = models.ToDictionary(model => model, _ => "healthy")
        };

    private static DetectorResult MapWorkerSummary(string summary) =>
        ChannelReliabilityResultMapper.MapProcessResult(
            keyId: 1,
            groupId: 10,
            model: DetectorModelNames.Sol,
            checkedAt: new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero),
            stdout: new BoundedOutput(summary, Truncated: false),
            exitCode: 0,
            executionFailed: false,
            timedOut: false,
            cancelled: false);

    private sealed class RecordingDetector(DetectorVerdict verdict) : IChannelReliabilityDetector
    {
        public List<(long KeyId, string Model)> Calls { get; } = [];

        public Task<DetectorResult> DetectAsync(
            DetectorBinding? binding,
            string? model,
            string? apiKey,
            long? groupId = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((binding?.KeyId ?? 0, model ?? string.Empty));
            return Task.FromResult(new DetectorResult
            {
                Model = model ?? string.Empty,
                ClaimedModel = DetectorModelNames.ToWorkerModel(model),
                Official = true,
                Verdict = verdict,
                Status = verdict == DetectorVerdict.Passed
                    ? ChannelReliabilityStatus.Passed
                    : ChannelReliabilityStatus.EvidenceInsufficient,
                ErrorCategory = DetectorErrorCategory.None
            });
        }
    }

    private sealed class SequenceDetector(
        IReadOnlyList<(DetectorVerdict Verdict, DetectorErrorCategory ErrorCategory)> sequence)
        : IChannelReliabilityDetector
    {
        private int _index;

        public Task<DetectorResult> DetectAsync(
            DetectorBinding? binding,
            string? model,
            string? apiKey,
            long? groupId = null,
            CancellationToken cancellationToken = default)
        {
            var item = sequence[Math.Min(_index++, sequence.Count - 1)];
            return Task.FromResult(new DetectorResult
            {
                KeyId = binding?.KeyId ?? 0,
                GroupId = groupId,
                Model = model ?? string.Empty,
                ClaimedModel = DetectorModelNames.ToWorkerModel(model),
                Official = true,
                Verdict = item.Verdict,
                Status = item.ErrorCategory == DetectorErrorCategory.None
                    ? (item.Verdict == DetectorVerdict.Passed
                        ? ChannelReliabilityStatus.Passed
                        : ChannelReliabilityStatus.EvidenceInsufficient)
                    : ChannelReliabilityStatus.Unavailable,
                ErrorCategory = item.ErrorCategory
            });
        }
    }

    private sealed class MemoryQuarantineStore(Action? onSave = null) : IChannelQuarantineStore
    {
        public List<ChannelQuarantineRecord> Saved { get; } = [];

        public IReadOnlyList<ChannelQuarantineRecord> LoadHistory() => Saved;

        public IReadOnlyList<ChannelQuarantineRecord> LoadLatest() => Saved;

        public IReadOnlyList<ChannelQuarantineRecord> GetActive(DateTimeOffset utcNow) =>
            Saved.Where(record => record.IsActiveAt(utcNow)).ToArray();

        public void Save(ChannelQuarantineRecord record)
        {
            Saved.RemoveAll(existing => existing.GroupId == record.GroupId);
            Saved.Add(record);
            onSave?.Invoke();
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
