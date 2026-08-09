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
                new DetectorResult { Verdict = verdict }.IsQuarantineEligible,
                $"{verdict} without an execution error must be quarantine eligible.");
            Assert(
                !new DetectorResult
                {
                    Verdict = verdict,
                    ErrorCategory = DetectorErrorCategory.Timeout
                }.IsQuarantineEligible,
                $"{verdict} with a timeout must not be quarantine eligible.");
        }

        foreach (var verdict in softVerdicts)
        {
            Assert(!ChannelReliabilityRules.IsHardVerdict(verdict), $"{verdict} must not be hard.");
        }

        var status = ChannelReliabilityRules.ResolveStatus(
        [
            new DetectorResult
            {
                Status = ChannelReliabilityStatus.Passed,
                Verdict = DetectorVerdict.JuiceMixed
            }
        ]);
        Assert(status == ChannelReliabilityStatus.Quarantined, "A hard verdict must quarantine the channel.");
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

        foreach (var forbidden in new[] { "secret", "token", "password", "cookie", "credential", "bearerToken" })
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
    }

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
                Verdict = verdict,
                Status = verdict == DetectorVerdict.Passed
                    ? ChannelReliabilityStatus.Passed
                    : ChannelReliabilityStatus.EvidenceInsufficient,
                ErrorCategory = DetectorErrorCategory.None
            });
        }
    }

    private sealed class MemoryQuarantineStore : IChannelQuarantineStore
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
