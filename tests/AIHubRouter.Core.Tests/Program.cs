using AIHubRouter.Core;
using AIHubRouter.Cli;
using System.Net;
using System.Text;
using System.Text.Json;

var tests = new (string Name, Action Body)[]
{
    ("Bearer token normalization", TestBearerNormalization),
    ("Token extraction from cookie", TestCookieTokenExtraction),
    ("Null provider availability is unavailable", TestNullProviderAvailabilityIsUnavailable),
    ("Usage stats map real TTFT and last use", TestUsageStatsMapRealTtftAndLastUse),
    ("Provider series request parses stable fields", TestProviderSeriesRequestParsesStableFields),
    ("Provider series rejects invalid payload", TestProviderSeriesRejectsInvalidPayload),
    ("Provider cache hit rate request parses percentages", TestProviderCacheHitRateRequestParsesPercentages),
    ("Provider model health survives invalid cache rate", TestProviderModelHealthSurvivesInvalidCacheRate),
    ("Provider series weight augments base score", TestProviderSeriesWeightAugmentsBaseScore),
    ("Provider cache hit rate augments quality score", TestProviderCacheHitRateAugmentsQualityScore),
    ("Missing provider cache hit rate is not rewarded", TestMissingProviderCacheHitRateIsNotRewarded),
    ("Provider cache hit rate failure falls back", TestProviderCacheHitRateFailureFallsBack),
    ("Provider series zero weight preserves score", TestProviderSeriesZeroWeightPreservesScore),
    ("Fresh generation does not hide stale provider samples", TestFreshGenerationDoesNotHideStaleProviderSamples),
    ("Sparse provider series is not rewarded", TestSparseProviderSeriesIsNotRewarded),
    ("Missing provider latency is not rewarded", TestMissingProviderLatencyIsNotRewarded),
    ("Stale provider group is excluded from quality", TestStaleProviderGroupIsExcludedFromQuality),
    ("Continuous freshness changes confidence", TestContinuousFreshnessChangesConfidence),
    ("Confidence penalizes latency score", TestConfidencePenalizesLatencyScore),
    ("Confidence impact controls latency penalty", TestConfidenceImpactControlsLatencyPenalty),
    ("Raw variance does not affect confidence", TestRawVarianceDoesNotAffectConfidence),
    ("One fresh sample has insufficient confidence", TestOneFreshSampleHasInsufficientConfidence),
    ("Stale last use is excluded", TestStaleLastUseIsExcluded),
    ("Future last use is excluded", TestFutureLastUseIsExcluded),
    ("Lowest available authorized group", TestLowestAvailableGroup),
    ("Blacklisted group is excluded", TestBlacklistedGroupIsExcluded),
    ("Blacklisted group is excluded from balanced evaluation", TestBalancedEvaluationExcludesBlacklistedGroup),
    ("Price range is a hard routing gate", TestPriceRangeIsHardRoutingGate),
    ("Confidence hard gate precedes price and speed", TestConfidenceHardGatePrecedesPriceAndSpeed),
    ("User rate override", TestUserRateOverride),
    ("Latest status controls eligibility", TestLatestStatusControlsEligibility),
    ("Stale status rejection", TestStaleStatusRejection),
    ("Balanced mode buys meaningful latency", TestBalancedModeBuysLatency),
    ("Balanced mode rejects catastrophic cheap latency", TestBalancedModeRejectsCatastrophicCheapLatency),
    ("Balanced mode buys meaningful speed gain", TestBalancedModeBuysSpeedForModerateGap),
    ("Balanced mode holds a marginal speed gain", TestBalancedModeHoldsMarginalSpeedGain),
    ("Balanced mode escapes extreme latency at double price", TestBalancedModeEscapesExtremeLatency),
    ("Balanced mode rejects weak latency value", TestBalancedModeRejectsWeakLatencyValue),
    ("Economy mode protects price", TestEconomyModeProtectsPrice),
    ("Economy latency utility is continuous", TestEconomyLatencyUtilityIsContinuous),
    ("Economy mode compresses sub-threshold speed gain", TestEconomyModeCompressesSubThresholdSpeedGain),
    ("Economy latency utility penalizes severe latency", TestEconomyLatencyUtilityPenalizesSevereLatency),
    ("Non-economy latency utility remains raw", TestNonEconomyLatencyUtilityRemainsRaw),
    ("Speed mode accepts larger price premium", TestSpeedModeAcceptsLargerPremium),
    ("Missing latency ranks last", TestMissingLatencyRanksLast),
    ("Zero multiplier window stays free", TestZeroMultiplierWindow),
    ("Close faster score keeps current group", TestCloseFasterScoreKeepsCurrentGroup),
    ("Close cheaper score keeps current group", TestCloseCheaperScoreKeepsCurrentGroup),
    ("Meaningful score advantage still switches", TestMeaningfulScoreAdvantageStillSwitches),
    ("Weighted speed winner switches immediately", TestWeightedSpeedWinnerSwitchesImmediately),
    ("Unknown current latency uses measured route", TestUnknownCurrentLatencyUsesMeasuredRoute),
    ("Price winner switches immediately", TestPriceWinnerSwitchesImmediately),
    ("Plain HTTP is rejected", TestPlainHttpIsRejected),
    ("Credential persistence defaults to enabled", TestCredentialPersistenceDefaultsToEnabled),
    ("AES settings roundtrip has no plaintext", TestAesSettingsRoundtrip),
    ("Credential protection failure preserves previous files", TestCredentialProtectionFailurePreservesPreviousFiles),
    ("Credential commit failure rolls back settings", TestCredentialCommitFailureRollsBackSettings),
    ("Pending persistence transaction recovers on load", TestPendingPersistenceTransactionRecoversOnLoad),
    ("Unavailable credential storage preserves unreadable files", TestUnavailableCredentialStoragePreservesUnreadableFiles),
    ("Empty credentials do not create a credential file", TestEmptyCredentialsDoNotCreateCredentialFile),
    ("Profile settings changes signal hot reload", TestProfileSettingsChangeSignal),
    ("Unavailable credential storage fails before settings write", TestUnavailableCredentialStorageIsAtomic),
    ("Legacy hard-gate settings are ignored", TestLegacyHardGateSettingsAreIgnored),
    ("Provider series settings roundtrip", TestProviderSeriesSettingsRoundtrip),
    ("Group stickiness persists as policy override", TestGroupStickinessPersistsAsPolicyOverride),
    ("Audit log writes valid JSON and rotates safely", TestAuditLogWritesValidJsonAndRotates),
    ("Dry run never updates a key", TestDryRunNeverUpdatesKey),
    ("Provider series cache and failure fallback", TestProviderSeriesCacheAndFailureFallback),
    ("Provider series cache rejects age-expired page", TestProviderSeriesCacheRejectsAgeExpiredPage),
    ("Provider series caller cancellation propagates", TestProviderSeriesCallerCancellationPropagates),
    ("Automatic route honors explicit multi-key selection", TestAutomaticRouteHonorsExplicitMultiKeySelection),
    ("Luna route filters failed model health groups", TestLunaRouteFiltersFailedModelHealthGroups),
    ("Luna health failure does not block primary route", TestLunaHealthFailureDoesNotBlockPrimaryRoute),
    ("Luna health loads after a primary-only cycle", TestLunaHealthLoadsAfterPrimaryOnlyCycle),
    ("Overlapping main and Luna keys are rejected", TestOverlappingMainAndLunaKeysAreRejected),
    ("Manual route updates selected keys and state", TestManualRouteUpdatesSelectedKeysAndState),
    ("Manual route honors explicit multi-key selection", TestManualRouteHonorsExplicitMultiKeySelection),
    ("Manual route rejects blacklisted group", TestManualRouteRejectsBlacklistedGroup),
    ("Manual route rejects out-of-range group", TestManualRouteRejectsOutOfRangeGroup),
    ("Manual route clears state after terminal authentication failure", TestManualRouteClearsStateAfterTerminalAuthenticationFailure),
    ("Manual route preserves changes across authentication retry", TestManualRoutePreservesChangesAcrossAuthenticationRetry),
    ("Encrypted settings roundtrip", TestEncryptedSettingsRoundtrip),
    ("Usable access token is reused", TestUsableAccessTokenIsReused),
    ("Expired access token refreshes first", TestExpiredAccessTokenRefreshesFirst),
    ("Rejected refresh falls back to login", TestRejectedRefreshFallsBackToLogin),
    ("Refresh API code falls back to login", TestRefreshApiCodeFallsBackToLogin),
    ("Refresh network failure does not log in", TestRefreshNetworkFailureDoesNotLogIn),
    ("Authentication API code is classified", TestAuthenticationApiCodeIsClassified),
    ("Login endpoint maps session", TestLoginEndpointMapsSession),
    ("Refresh endpoint maps rotated session", TestRefreshEndpointMapsRotatedSession),
    ("Refresh keeps token when server omits rotation", TestRefreshKeepsTokenWhenServerOmitsRotation),
    ("Authentication error hides server message", TestAuthenticationErrorHidesServerMessage),
    ("Business error hides server message", TestBusinessErrorHidesServerMessage),
    ("Interactive login requirement is rejected", TestInteractiveLoginRequirementIsRejected),
    ("Authentication rejection shows status hint", TestAuthenticationRejectionShowsStatusHint),
    ("Authentication validation error shows format hint", TestAuthenticationValidationErrorShowsFormatHint),
    ("Cloudflare JS challenge is detected", TestCloudflareJsChallengeIsDetected),
    ("Cloudflare interactive challenge is detected", TestCloudflareInteractiveChallengeIsDetected),
    ("Cloudflare challenge solves and retries with cookies", TestCloudflareChallengeSolverRetriesWithCookies),
    ("Cloudflare solver failure does not retry forever", TestCloudflareSolverFailureDoesNotRetryForever),
    ("JSON 403 business error is not a Cloudflare challenge", TestJsonBusinessErrorIsNotCloudflareChallenge),
    ("Empty key selection roundtrips", TestEmptyKeySelectionRoundtrips),
    ("First key selection chooses first active key", TestFirstKeySelectionChoosesFirstActiveKey),
    ("Initialized empty key selection stays empty", TestInitializedEmptyKeySelectionStaysEmpty),
    ("Reliability model capability selection", ChannelReliabilityTests.TestDetectorModelNamesAndProbeSelection),
    ("Reliability skips failed and unknown models", ChannelReliabilityTests.TestSelectProbeModelsSkipsFailedAndUnknown),
    ("Reliability hard verdict classification", ChannelReliabilityTests.TestHardVerdictClassification),
    ("Reliability quarantine expires after 24 hours", ChannelReliabilityTests.TestQuarantineExpiresAfterTwentyFourHours),
    ("Reliability quarantine store roundtrip", ChannelReliabilityTests.TestJsonQuarantineStoreRoundtripAndActiveFiltering),
    ("Reliability serialization excludes secrets", ChannelReliabilityTests.TestReliabilitySerializationExcludesSecrets),
    ("Reliability keeps Key bindings and model capabilities independent", ChannelReliabilityTests.TestMonitorKeepsKeyBindingsIndependent),
    ("Reliability quarantines hard detector verdicts", ChannelReliabilityTests.TestMonitorQuarantinesHardVerdict)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Body();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

if (Environment.GetEnvironmentVariable("AIHUB_SMOKE_TEST") == "1")
{
    try
    {
        using var client = new AIHubClient("https://aihub.top");
        var pages = new[] { await client.GetGroupUsageStatsAsync(
            "openai",
            GroupUsageEstimator.DefaultSampleLimit) };
        var providers = GroupUsageEstimator.Estimate(
            pages,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(15));
        var trustedCount = providers.Count(provider => provider.Available);
        Assert(providers.Count > 0, "Public usage stats endpoint returned no entries.");
        Assert(trustedCount > 0, "Public usage stats produced no fresh, trusted groups.");
        Console.WriteLine(
            $"PASS Public API smoke test ({providers.Count} groups, {trustedCount} trusted)");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL Public API smoke test: {exception.Message}");
    }
}

return failures == 0 ? 0 : 1;

static void TestBearerNormalization()
{
    Assert(CredentialParser.NormalizeBearerToken("Authorization: Bearer abc.def") == "abc.def", "Header was not normalized.");
    Assert(CredentialParser.NormalizeBearerToken("Bearer token") == "token", "Bearer prefix was not removed.");
}

static void TestCookieTokenExtraction()
{
    var token = CredentialParser.TryExtractTokenFromCookie("theme=dark; auth_token=abc%2Edef; lang=zh");
    Assert(token == "abc.def", "auth_token cookie was not decoded.");
}

static void TestNullProviderAvailabilityIsUnavailable()
{
    var handler = new StubHttpMessageHandler(_ => JsonResponse("""
        {
          "code": 0,
          "message": "success",
          "data": {
            "items": [{
              "code": "A016-Free",
              "group_id": 51,
              "platform": "openai",
              "rate_multiplier": 0.005,
              "avg_ttft_ms": null,
              "sample_count": 0,
              "last_sample_at": null
            }],
            "total": 1,
            "sample_limit": 100
          }
        }
        """));
    using var client = new AIHubClient("https://example.test", messageHandler: handler);

    var now = new DateTimeOffset(2026, 7, 25, 12, 34, 45, TimeSpan.Zero);
    var usageStats = client.GetGroupUsageStatsAsync("openai").GetAwaiter().GetResult();
    var providers = GroupUsageEstimator.Estimate([usageStats], now, TimeSpan.FromMinutes(15));

    Assert(providers.Count == 1 && !providers[0].Available,
        "A usage group without samples was not treated as unavailable.");
}

static void TestUsageStatsMapRealTtftAndLastUse()
{
    var lastUsed = new DateTimeOffset(2026, 7, 25, 12, 34, 45, TimeSpan.Zero);
    var page = new GroupUsageStatsPage
    {
        SampleLimit = 100,
        Items =
        [
            new GroupUsageStat
            {
                Code = "A004-Pro",
                Platform = "openai",
                RateMultiplier = 0.17,
                AverageTtftMs = 3011.6,
                SampleCount = 100,
                LastSampleAt = lastUsed,
                GroupId = 12
            }
        ]
    };

    var provider = GroupUsageEstimator.Estimate(
        [page],
        lastUsed,
        TimeSpan.FromMinutes(15)).Single();

    Assert(provider.GroupId == 12 && provider.FirstTokenLatencyMs == 3011.6,
        "Real usage TTFT was not mapped to the route candidate.");
    Assert(provider.CheckedAt == lastUsed && provider.UsageSampleCount == 100 && provider.Available,
        "Last use or sample count was not mapped to candidate freshness.");
    Assert(provider.LatencyConfidence is > 0.99 and <= 1,
        "Fresh aggregate confidence did not reflect sample volume.");
}

static void TestProviderSeriesRequestParsesStableFields()
{
    Uri? requestUri = null;
    var handler = new StubHttpMessageHandler(request =>
    {
        requestUri = request.RequestUri;
        return JsonResponse("""
            {
              "code": 0,
              "message": "success",
              "data": {
                "generated_at": "2026-08-08T10:00:00Z",
                "range": "6h",
                "items": [{
                  "group_id": 7,
                  "probe": [
                    [1786183200000, 1, 100, 90.2, 19],
                    [1786183260000, 0, 0, "provider_probe", "http_status"],
                    [1786183320000, 1, 300, null, null]
                  ],
                  "user_ttft": [
                    {"at":"2026-08-08T09:58:00Z","avg_ttft_ms":1000,"sample_count":2,"has_data":true},
                    {"at":"2026-08-08T09:59:00Z","avg_ttft_ms":500,"sample_count":1,"has_data":true},
                    {"at":"2026-08-08T10:00:00Z","avg_ttft_ms":1,"sample_count":99,"has_data":false}
                  ]
                },{
                  "group_id": 9007199254740993,
                  "probe": [[1786183200000, 1, 100]],
                  "user_ttft": {"at":"2026-08-08T10:00:00Z","avg_ttft_ms":100,"sample_count":2,"has_data":true}
                }]
              }
            }
            """);
    });
    using var client = new AIHubClient("https://example.test", messageHandler: handler);

    var page = client.GetProviderSeriesAsync("6h", "Asia/Shanghai")
        .GetAwaiter()
        .GetResult();
    var metrics = page.Groups[7];

    var query = requestUri?.Query ?? string.Empty;
    Assert(requestUri?.AbsolutePath == "/api/v1/public/providers/series",
        "Provider series used the wrong endpoint.");
    Assert(query.Contains("range=6h", StringComparison.Ordinal) &&
           query.Contains("timezone=Asia%2FShanghai", StringComparison.OrdinalIgnoreCase),
        "Provider series did not URI-encode its query parameters.");
    Assert(metrics.ProbeSampleCount == 3 &&
           Math.Abs(metrics.ProbeSuccessRate!.Value - 2d / 3d) < 0.0001,
        "Probe success rate was not aggregated.");
    Assert(Math.Abs(metrics.AverageProbeLatencyMs!.Value - 200) < 0.0001,
        "Failed probes affected successful probe latency.");
    Assert(metrics.UserTtftSampleCount == 3 &&
           Math.Abs(metrics.AverageUserTtftMs!.Value - 2500d / 3d) < 0.0001,
        "User TTFT buckets were not weighted by sample_count.");
    Assert(page.Groups.ContainsKey(9_007_199_254_740_993),
        "A large Int64 group_id lost precision during parsing.");
}

static void TestProviderSeriesRejectsInvalidPayload()
{
    var handler = new StubHttpMessageHandler(_ => JsonResponse("""
        {"code":0,"message":"success","data":{"range":"6h"}}
        """));
    using var client = new AIHubClient("https://example.test", messageHandler: handler);

    var rejected = false;
    try
    {
        client.GetProviderSeriesAsync("6h", "Asia/Shanghai").GetAwaiter().GetResult();
    }
    catch (AIHubApiException)
    {
        rejected = true;
    }

    Assert(rejected, "Provider series accepted a payload without items.");
}

static void TestProviderCacheHitRateRequestParsesPercentages()
{
    Uri? requestUri = null;
    var handler = new StubHttpMessageHandler(request =>
    {
        requestUri = request.RequestUri;
        return JsonResponse("""
            {
              "code": 0,
              "message": "success",
              "data": {
                "generated_at": "2026-08-08T10:00:00Z",
                "items": [
                  {"group_id": 7, "cache_hit_rate": "82.88%"},
                  {"group_id": 7, "cache_hit_rate": "77.12%"},
                  {"group_id": 8, "cache_hit_rate": "样本不足"},
                  {"group_id": 9, "cache_hit_rate": "101%"},
                  {"group_id": "10", "cache_hit_rate": 0.75}
                ]
              }
            }
            """);
    });
    using var client = new AIHubClient("https://example.test", messageHandler: handler);

    var page = client.GetProviderCacheHitRatesAsync("Asia/Shanghai")
        .GetAwaiter()
        .GetResult();
    var query = requestUri?.Query ?? string.Empty;

    Assert(requestUri?.AbsolutePath == "/api/v1/public/providers",
        "Provider cache hit rate used the wrong endpoint.");
    Assert(query.Contains("timezone=Asia%2FShanghai", StringComparison.OrdinalIgnoreCase),
        "Provider cache hit rate did not URI-encode its timezone.");
    Assert(page.Groups.TryGetValue(7, out var average) &&
           Math.Abs(average - 0.80) < 0.0001,
        "Duplicate provider cache hit rates were not averaged after percentage parsing.");
    Assert(page.Groups.TryGetValue(10, out var ratio) &&
           Math.Abs(ratio - 0.75) < 0.0001,
        "Ratio-form cache hit rate was not parsed.");
    Assert(!page.Groups.ContainsKey(8) && !page.Groups.ContainsKey(9),
        "Invalid or insufficient cache hit rate samples entered the result.");
}

static void TestProviderModelHealthSurvivesInvalidCacheRate()
{
    var handler = new StubHttpMessageHandler(_ => JsonResponse("""
        {
          "code": 0,
          "message": "success",
          "data": {
            "items": [
              {"group_id": 21, "cache_hit_rate": "样本不足", "model_health": {"luna": "failed", "sol": "healthy"}},
              {"group_id": 21, "cache_hit_rate": "90%", "model_health": {"luna": "healthy"}},
              {"group_id": 22, "model_health": {"luna": "healthy"}}
            ]
          }
        }
        """));
    using var client = new AIHubClient("https://example.test", messageHandler: handler);

    var page = client.GetProviderCacheHitRatesAsync("Asia/Shanghai")
        .GetAwaiter()
        .GetResult();

    Assert(!page.Groups.ContainsKey(22), "A missing cache rate should not create a cache score.");
    Assert(page.Groups.TryGetValue(21, out var rate) && Math.Abs(rate - 0.90) < 0.0001,
        "Valid cache samples should still be averaged after health parsing.");
    Assert(page.ModelHealthByGroup.TryGetValue(21, out var groupHealth) &&
           groupHealth.TryGetValue("luna", out var status) &&
           status.Equals("failed", StringComparison.OrdinalIgnoreCase),
        "Duplicate model health entries must retain failed precedence.");
    Assert(page.ModelHealthByGroup.ContainsKey(22),
        "Model health must be retained even when cache_hit_rate is missing.");
}

static void TestProviderSeriesWeightAugmentsBaseScore()
{
    var now = DateTimeOffset.UtcNow;
    var providers = new[]
    {
        Provider(1, 0.02, true, 1, now, latency: 1_000, cacheHitRate: 0.01),
        Provider(2, 0.021, true, 1, now, latency: 900, cacheHitRate: 0.99)
    };
    var groups = new[] { Group(1), Group(2) };
    var metrics = new Dictionary<long, ProviderSeriesMetrics>
    {
        [1] = new(1, 0.50, 1_000, 1_000, 20, 20, now),
        [2] = new(2, 1.00, 500, 500, 20, 20, now)
    };
    var baseEvaluation = RoutingEngine.Evaluate(
        providers,
        groups,
        new Dictionary<long, double>(),
        Policy(RoutingMode.Balanced) with { ProviderSeriesWeight = 0 },
        now,
        metrics);
    var weightedEvaluation = RoutingEngine.Evaluate(
        providers,
        groups,
        new Dictionary<long, double>(),
        Policy(RoutingMode.Balanced) with { ProviderSeriesWeight = 0.50 },
        now,
        metrics);
    var baseScore = RoutingEngine.CalculateWeightedScore(
        baseEvaluation,
        baseEvaluation.EligibleCandidates.Single(candidate => candidate.Group.Id == 2));
    var weightedScore = RoutingEngine.CalculateWeightedScore(
        weightedEvaluation,
        weightedEvaluation.EligibleCandidates.Single(candidate => candidate.Group.Id == 2));

    Assert(baseScore is { } original &&
           weightedScore is { } augmented &&
           augmented > original + 0.40,
        "Provider quality did not augment the existing tradeoff score.");
}

static void TestProviderCacheHitRateAugmentsQualityScore()
{
    var now = DateTimeOffset.UtcNow;
    var evaluation = RoutingEngine.Evaluate(
        [
            Provider(1, 0.02, true, 1, now, latency: 1_000, cacheHitRate: 0.10),
            Provider(2, 0.021, true, 1, now, latency: 1_000, cacheHitRate: 0.90)
        ],
        [Group(1), Group(2)],
        new Dictionary<long, double>(),
        Policy(RoutingMode.Balanced) with { ProviderSeriesWeight = 0.50 },
        now,
        new Dictionary<long, ProviderSeriesMetrics>
        {
            [1] = new(1, 1, 1_000, 1_000, 20, 20, now),
            [2] = new(2, 1, 1_000, 1_000, 20, 20, now)
        });

    Assert(evaluation.ProviderSeriesScores[2] > evaluation.ProviderSeriesScores[1] + 0.45,
        "A higher provider cache hit rate did not receive the increased quality weight.");
}

static void TestMissingProviderCacheHitRateIsNotRewarded()
{
    var now = DateTimeOffset.UtcNow;
    var evaluation = RoutingEngine.Evaluate(
        [
            Provider(1, 0.02, true, 1, now, latency: 1_000),
            Provider(2, 0.021, true, 1, now, latency: 1_000, cacheHitRate: 0.90)
        ],
        [Group(1), Group(2)],
        new Dictionary<long, double>(),
        Policy(RoutingMode.Balanced) with { ProviderSeriesWeight = 0.50 },
        now,
        new Dictionary<long, ProviderSeriesMetrics>
        {
            [1] = new(1, 1, 1_000, 1_000, 20, 20, now),
            [2] = new(2, 1, 1_000, 1_000, 20, 20, now)
        });

    Assert(Math.Abs(evaluation.ProviderSeriesScores[1] - evaluation.ProviderSeriesScores[2]) < 1e-12,
        "A partial provider cache hit rate set rewarded the candidate with data.");
}

static void TestProviderCacheHitRateFailureFallsBack()
{
    var now = DateTimeOffset.UtcNow;
    var api = new StubAIHubApiClient(
        now,
        providerCacheHitRates: _ => throw new HttpRequestException("upstream detail"));
    var settings = new PersistentAppSettings
    {
        KeySelectionInitialized = true,
        SelectedKeyIds = [10]
    };
    using var service = new RoutingService(
        settings,
        new PersistentCredentials { BearerToken = "test-token" },
        new MemoryRouteStateStore(),
        new StubAIHubClientFactory(api),
        utcNow: () => now);

    var result = service.RunOnceAsync(dryRun: true).GetAwaiter().GetResult();

    Assert(result.ProviderCacheHitRateStatus.IsDegraded &&
           !result.ProviderCacheHitRateStatus.Available &&
           result.Decision.Target is not null,
        "A cache hit rate API failure did not preserve base routing.");
    Assert(!result.ProviderCacheHitRateStatus.Message.Contains("upstream detail", StringComparison.Ordinal),
        "Cache hit rate failure exposed the upstream error detail.");
}

static void TestProviderSeriesZeroWeightPreservesScore()
{
    var now = DateTimeOffset.UtcNow;
    var providers = new[]
    {
        Provider(1, 0.02, true, 1, now, latency: 1_000, cacheHitRate: 0.01),
        Provider(2, 0.021, true, 1, now, latency: 900, cacheHitRate: 0.99)
    };
    var policy = Policy(RoutingMode.Balanced) with { ProviderSeriesWeight = 0 };
    var withoutSeries = RoutingEngine.Evaluate(
        providers,
        [Group(1), Group(2)],
        new Dictionary<long, double>(),
        policy,
        now);
    var withSeries = RoutingEngine.Evaluate(
        providers,
        [Group(1), Group(2)],
        new Dictionary<long, double>(),
        policy,
        now,
        new Dictionary<long, ProviderSeriesMetrics>
        {
            [1] = new(1, 0, 10_000, 10_000, 20, 20, now),
            [2] = new(2, 1, 1, 1, 20, 20, now)
        });
    var withoutScore = RoutingEngine.CalculateWeightedScore(
        withoutSeries,
        withoutSeries.EligibleCandidates.Single(candidate => candidate.Group.Id == 2));
    var withScore = RoutingEngine.CalculateWeightedScore(
        withSeries,
        withSeries.EligibleCandidates.Single(candidate => candidate.Group.Id == 2));

    Assert(withoutScore is { } original &&
           withScore is { } compatible &&
           Math.Abs(original - compatible) < 1e-12,
        "Zero provider series weight changed the legacy score.");
}

static void TestFreshGenerationDoesNotHideStaleProviderSamples()
{
    var now = DateTimeOffset.UtcNow;
    var api = new StubAIHubApiClient(
        now,
        providerSeries: _ => new ProviderSeriesPage(
            now,
            "6h",
            new Dictionary<long, ProviderSeriesMetrics>
            {
                [2] = new(2, 1, 500, 500, 20, 20, now.AddHours(-1))
            }));
    using var service = new RoutingService(
        new PersistentAppSettings
        {
            KeySelectionInitialized = true,
            SelectedKeyIds = [10]
        },
        new PersistentCredentials { BearerToken = "test-token" },
        new MemoryRouteStateStore(),
        new StubAIHubClientFactory(api),
        utcNow: () => now);

    var result = service.RunOnceAsync(dryRun: true).GetAwaiter().GetResult();

    Assert(!result.ProviderSeriesStatus.Available,
        "A fresh generated_at value hid stale provider samples.");
    Assert(result.Providers.Single(provider => provider.GroupId == 2).CacheHitRate is { } rate &&
           Math.Abs(rate - 0.80) < 0.0001,
        "The provider cache hit rate was not attached to the routed provider.");
}

static void TestSparseProviderSeriesIsNotRewarded()
{
    var now = DateTimeOffset.UtcNow;
    var evaluation = RoutingEngine.Evaluate(
        [
            Provider(1, 0.02, true, 1, now, latency: 1_000),
            Provider(2, 0.021, true, 1, now, latency: 900)
        ],
        [Group(1), Group(2)],
        new Dictionary<long, double>(),
        Policy(RoutingMode.Balanced) with { ProviderSeriesWeight = 0.50 },
        now,
        new Dictionary<long, ProviderSeriesMetrics>
        {
            [1] = new(1, 0.90, 1_000, 1_000, 20, 20, now),
            [2] = new(2, 1.00, null, null, 1, 0, now)
        });

    Assert(evaluation.ProviderSeriesScores.ContainsKey(1) &&
           !evaluation.ProviderSeriesScores.ContainsKey(2),
        "A sparse one-probe provider received a full quality score.");
}

static void TestMissingProviderLatencyIsNotRewarded()
{
    var now = DateTimeOffset.UtcNow;
    var evaluation = RoutingEngine.Evaluate(
        [
            Provider(1, 0.02, true, 1, now, latency: 1_000),
            Provider(2, 0.021, true, 1, now, latency: 900)
        ],
        [Group(1), Group(2)],
        new Dictionary<long, double>(),
        Policy(RoutingMode.Balanced) with { ProviderSeriesWeight = 0.50 },
        now,
        new Dictionary<long, ProviderSeriesMetrics>
        {
            [1] = new(1, 1.00, null, 1_000, 20, 20, now),
            [2] = new(2, 0.50, 500, 500, 20, 20, now)
        });

    Assert(!evaluation.ProviderSeriesScores.ContainsKey(1) &&
           evaluation.ProviderSeriesScores.ContainsKey(2),
        "A provider without probe latency received a quality score.");
}

static void TestStaleProviderGroupIsExcludedFromQuality()
{
    var now = DateTimeOffset.UtcNow;
    var evaluation = RoutingEngine.Evaluate(
        [
            Provider(1, 0.02, true, 1, now, latency: 1_000),
            Provider(2, 0.021, true, 1, now, latency: 900)
        ],
        [Group(1), Group(2)],
        new Dictionary<long, double>(),
        Policy(RoutingMode.Balanced) with { ProviderSeriesWeight = 0.50 },
        now,
        new Dictionary<long, ProviderSeriesMetrics>
        {
            [1] = new(1, 1.00, 1_000, 1_000, 20, 20, now.AddHours(-1)),
            [2] = new(2, 0.50, 500, 500, 20, 20, now)
        });

    Assert(!evaluation.ProviderSeriesScores.ContainsKey(1) &&
           evaluation.ProviderSeriesScores.ContainsKey(2),
        "A stale provider group remained in quality comparison.");
}

static void TestStaleLastUseIsExcluded()
{
    var now = new DateTimeOffset(2026, 7, 25, 12, 30, 0, TimeSpan.Zero);
    var page = new GroupUsageStatsPage
    {
        Items =
        [
            new GroupUsageStat
            {
                Code = "stale-group",
                Platform = "openai",
                RateMultiplier = 0.01,
                AverageTtftMs = 500,
                SampleCount = 100,
                LastSampleAt = now.AddHours(-3),
                GroupId = 2
            }
        ]
    };
    var providers = GroupUsageEstimator.Estimate(
        [page],
        now,
        TimeSpan.FromMinutes(15));

    var evaluation = RoutingEngine.Evaluate(
        providers,
        [Group(2)],
        new Dictionary<long, double>(),
        Policy(RoutingMode.Balanced),
        now);

    Assert(providers.Single().LatencyConfidence == 0 &&
           evaluation.Recommended is null &&
           evaluation.EligibleCandidates.Count == 0,
        "A group whose last real request was hours ago entered the candidate pool.");
}

static void TestContinuousFreshnessChangesConfidence()
{
    var now = new DateTimeOffset(2026, 7, 25, 12, 30, 0, TimeSpan.Zero);
    var fresh = GroupUsageEstimator.Estimate(
        [UsagePage(100, 1_000, 100, now)],
        now,
        TimeSpan.FromMinutes(15)).Single();
    var halfOld = GroupUsageEstimator.Estimate(
        [UsagePage(100, 1_000, 100, now.AddMinutes(-7.5))],
        now,
        TimeSpan.FromMinutes(15)).Single();

    Assert(fresh.LatencyConfidence is { } freshConfidence &&
           halfOld.LatencyConfidence is { } halfOldConfidence &&
           freshConfidence > halfOldConfidence,
        "Confidence did not decrease continuously as the last sample aged.");
    Assert(halfOld.LatencyConfidence is > 0 &&
           fresh.LatencyConfidence is { } currentFreshConfidence &&
           halfOld.LatencyConfidence < currentFreshConfidence,
        "Continuous freshness collapsed to a discrete window decision.");
}

static void TestConfidencePenalizesLatencyScore()
{
    var now = DateTimeOffset.UtcNow;
    var highConfidence = RoutingEngine.Evaluate(
        [
            Provider(1, 0.02, true, 1, now, latency: 10_000, confidence: 1),
            Provider(2, 0.021, true, 1, now, latency: 1_000, confidence: 1)
        ],
        [Group(1), Group(2)],
        new Dictionary<long, double>(),
        Policy(RoutingMode.Balanced),
        now);
    var lowConfidence = RoutingEngine.Evaluate(
        [
            Provider(1, 0.02, true, 1, now, latency: 10_000, confidence: 1),
            Provider(2, 0.021, true, 1, now, latency: 1_000, confidence: 0.95)
        ],
        [Group(1), Group(2)],
        new Dictionary<long, double>(),
        Policy(RoutingMode.Balanced),
        now);

    var highScore = RoutingEngine.CalculateWeightedScore(
        highConfidence,
        highConfidence.EligibleCandidates.Single(candidate => candidate.Group.Id == 2));
    var lowScore = RoutingEngine.CalculateWeightedScore(
        lowConfidence,
        lowConfidence.EligibleCandidates.Single(candidate => candidate.Group.Id == 2));

    Assert(highScore is { } high && high > 0 && lowScore is { } low && low < high,
        "Low-confidence latency was not penalized in the weighted score.");
}

static void TestConfidenceImpactControlsLatencyPenalty()
{
    var now = DateTimeOffset.UtcNow;
    var providers = new[]
    {
        Provider(1, 0.02, true, 1, now, latency: 10_000, confidence: 1),
        Provider(2, 0.021, true, 1, now, latency: 1_000, confidence: 0.95)
    };
    var groups = new[] { Group(1), Group(2) };
    var withoutPenalty = RoutingEngine.Evaluate(
        providers,
        groups,
        new Dictionary<long, double>(),
        Policy(RoutingMode.Balanced) with { ConfidenceImpact = 0 },
        now);
    var withStrongPenalty = RoutingEngine.Evaluate(
        providers,
        groups,
        new Dictionary<long, double>(),
        Policy(RoutingMode.Balanced) with { ConfidenceImpact = 2 },
        now);

    var scoreWithoutPenalty = RoutingEngine.CalculateWeightedScore(
        withoutPenalty,
        withoutPenalty.EligibleCandidates.Single(candidate => candidate.Group.Id == 2));
    var scoreWithStrongPenalty = RoutingEngine.CalculateWeightedScore(
        withStrongPenalty,
        withStrongPenalty.EligibleCandidates.Single(candidate => candidate.Group.Id == 2));

    Assert(scoreWithoutPenalty is { } unpenalized &&
           scoreWithStrongPenalty is { } penalized &&
           unpenalized > penalized,
        "Changing confidence impact did not change the weighted latency score.");
}

static void TestRawVarianceDoesNotAffectConfidence()
{
    var now = DateTimeOffset.UtcNow;
    GroupUsageStatsPage Page(long groupId, params double[] latencies) => new()
    {
        Items =
        [
            new GroupUsageStat
            {
                Code = $"group-{groupId}",
                Platform = "openai",
                RateMultiplier = 0.1,
                SampleCount = latencies.Length,
                LastSampleAt = now,
                GroupId = groupId,
                Samples = latencies.Select(latency => new GroupUsageSample
                {
                    Timestamp = now,
                    FirstTokenLatencyMs = latency
                }).ToList()
            }
        ]
    };

    var stable = GroupUsageEstimator.Estimate(
        [Page(1, 1_000, 1_000, 1_000)], now, TimeSpan.FromMinutes(15)).Single();
    var variable = GroupUsageEstimator.Estimate(
        [Page(2, 100, 1_000, 1_900)], now, TimeSpan.FromMinutes(15)).Single();

    Assert(stable.LatencyConfidence is { } stableConfidence &&
           variable.LatencyConfidence is { } variableConfidence &&
           Math.Abs(stableConfidence - variableConfidence) < 1e-12,
        "Latency variance unexpectedly changed confidence.");
}

static void TestOneFreshSampleHasInsufficientConfidence()
{
    var now = new DateTimeOffset(2026, 7, 25, 12, 30, 0, TimeSpan.Zero);
    var provider = GroupUsageEstimator.Estimate(
        [UsagePage(1, 1_000, 1, now)],
        now,
        TimeSpan.FromMinutes(15)).Single();

    Assert(provider.LatencyConfidence is > 0 and < GroupUsageEstimator.MinimumConfidence,
        "A single sample did not receive low confidence.");
    Assert(!provider.Available,
        "A group backed by only one request entered the candidate pool.");
}

static void TestFutureLastUseIsExcluded()
{
    var now = new DateTimeOffset(2026, 7, 25, 12, 30, 0, TimeSpan.Zero);
    var provider = GroupUsageEstimator.Estimate(
        [UsagePage(100, 1_000, 100, now.AddMinutes(2))],
        now,
        TimeSpan.FromMinutes(15)).Single();

    Assert(provider.LatencyConfidence == 0 && !provider.Available,
        "An implausible future sample timestamp was treated as trustworthy.");
}

static GroupUsageStatsPage UsagePage(
    int sampleLimit,
    double averageTtftMs,
    int sampleCount,
    DateTimeOffset lastSampleAt) => new()
{
    SampleLimit = sampleLimit,
    Items =
    [
        new GroupUsageStat
        {
            Code = "Group 2",
            Platform = "openai",
            RateMultiplier = 0.02,
            AverageTtftMs = averageTtftMs,
            SampleCount = sampleCount,
            LastSampleAt = lastSampleAt,
            GroupId = 2
        }
    ]
};

static void TestLowestAvailableGroup()
{
    var now = DateTimeOffset.UtcNow;
    var providers = new[]
    {
        Provider(1, 0.02, available: false, success: 1, now),
        Provider(2, 0.04, available: true, success: 0.8, now),
        Provider(3, 0.03, available: true, success: 0.9, now)
    };
    var groups = new[] { Group(2), Group(3) };

    var result = RoutingEngine.SelectCheapest(providers, groups, new Dictionary<long, double>(), Criteria(), now);
    Assert(result?.Group.Id == 3, "Did not select the cheapest available authorized group.");
}

static void TestBlacklistedGroupIsExcluded()
{
    var now = DateTimeOffset.UtcNow;
    var providers = new[]
    {
        Provider(1, 0.01, available: true, success: 1, now),
        Provider(2, 0.05, available: true, success: 1, now)
    };

    var result = RoutingEngine.SelectCheapest(
        providers,
        new[] { Group(1), Group(2) },
        new Dictionary<long, double>(),
        Criteria() with { BlacklistedGroupIds = [1] },
        now);

    Assert(result?.Group.Id == 2, "A blacklisted group entered the candidate pool.");
}

static void TestBalancedEvaluationExcludesBlacklistedGroup()
{
    var now = DateTimeOffset.UtcNow;
    var evaluation = RoutingEngine.Evaluate(
        new[]
        {
            Provider(1, 0.01, available: true, success: 1, now),
            Provider(2, 0.05, available: true, success: 1, now)
        },
        new[] { Group(1), Group(2) },
        new Dictionary<long, double>(),
        new BalancedRoutingPolicy { BlacklistedGroupIds = [1] },
        now);

    Assert(evaluation.Recommended?.Group.Id == 2 &&
           evaluation.EligibleCandidates.All(candidate => candidate.Group.Id != 1),
        "A blacklisted group entered balanced evaluation.");
}

static void TestPriceRangeIsHardRoutingGate()
{
    var now = DateTimeOffset.UtcNow;
    var evaluation = RoutingEngine.Evaluate(
        new[]
        {
            Provider(1, 0.04, true, 1, now, latency: 100),
            Provider(2, 0.10, true, 1, now, latency: 2_000),
            Provider(3, 0.01, true, 1, now, latency: 50)
        },
        new[] { Group(1), Group(2), Group(3) },
        new Dictionary<long, double> { [3] = 0.151 },
        new BalancedRoutingPolicy
        {
            MinimumPriceMultiplier = 0.05,
            MaximumPriceMultiplier = 0.15
        },
        now);

    Assert(evaluation.Recommended?.Group.Id == 2 &&
           evaluation.EligibleCandidates.Count == 1 &&
           evaluation.EligibleCandidates.Single().Group.Id == 2,
        "A price outside the configured range entered routing evaluation.");
}

static void TestConfidenceHardGatePrecedesPriceAndSpeed()
{
    var now = DateTimeOffset.UtcNow;
    var providers = new[]
    {
        Provider(1, 0.001, true, 1, now, latency: 100, confidence: 0.89),
        Provider(2, 0.02, true, 1, now, latency: 2_000, confidence: 0.90)
    };
    var groups = new[] { Group(1), Group(2) };

    var cheapest = RoutingEngine.SelectCheapest(
        providers,
        groups,
        new Dictionary<long, double>(),
        Criteria(),
        now);
    var evaluation = RoutingEngine.Evaluate(
        providers,
        groups,
        new Dictionary<long, double>(),
        Policy(RoutingMode.Balanced),
        now);

    Assert(cheapest?.Group.Id == 2,
        "The cheapest selector admitted a low-confidence group.");
    Assert(evaluation.Recommended?.Group.Id == 2 &&
           evaluation.EligibleCandidates.All(candidate => candidate.Group.Id != 1),
        "Price or speed scoring ran before the confidence hard gate.");
}

static void TestUserRateOverride()
{
    var now = DateTimeOffset.UtcNow;
    var providers = new[]
    {
        Provider(1, 0.02, true, 1, now),
        Provider(2, 0.04, true, 1, now)
    };
    var rates = new Dictionary<long, double> { [1] = 0.10, [2] = 0.01 };

    var result = RoutingEngine.SelectCheapest(providers, new[] { Group(1), Group(2) }, rates, Criteria(), now);
    Assert(result?.Group.Id == 2 && result.HasUserRateOverride, "User rate override was not used.");
}

static void TestLatestStatusControlsEligibility()
{
    var now = DateTimeOffset.UtcNow;
    var providers = new[]
    {
        Provider(1, 0.01, available: true, success: 0, now, warning: true),
        Provider(2, 0.005, available: false, success: 1, now),
        Provider(3, 0.05, available: true, success: 1, now)
    };

    var result = RoutingEngine.SelectCheapest(
        providers,
        new[] { Group(1), Group(2), Group(3) },
        new Dictionary<long, double>(),
        Criteria(),
        now);
    Assert(result?.Group.Id == 1 && result.Provider.HasWarnings,
        "A warning status was rejected or the six-hour success rate affected eligibility.");
}

static void TestStaleStatusRejection()
{
    var now = DateTimeOffset.UtcNow;
    var providers = new[]
    {
        Provider(1, 0.01, true, 1, now - TimeSpan.FromMinutes(16)),
        Provider(2, 0.05, true, 1, now)
    };

    var result = RoutingEngine.SelectCheapest(providers, new[] { Group(1), Group(2) }, new Dictionary<long, double>(), Criteria(), now);
    Assert(result?.Group.Id == 2, "Stale provider status was not rejected.");
}

static void TestBalancedModeBuysLatency()
{
    var now = DateTimeOffset.UtcNow;
    var providers = new[]
    {
        Provider(1, 0.02, true, 0.99, now, latency: 10_000),
        Provider(2, 0.022, true, 0.99, now, latency: 1_000)
    };
    var evaluation = RoutingEngine.Evaluate(
        providers,
        new[] { Group(1), Group(2) },
        new Dictionary<long, double>(),
        Policy(RoutingMode.Balanced),
        now);

    Assert(evaluation.Recommended?.Group.Id == 2, "Balanced mode did not buy a large latency improvement.");
}

static void TestEconomyModeProtectsPrice()
{
    var now = DateTimeOffset.UtcNow;
    var providers = new[]
    {
        Provider(1, 0.02, true, 0.99, now, latency: 2_000),
        Provider(2, 0.03, true, 0.99, now, latency: 1_000)
    };
    var evaluation = RoutingEngine.Evaluate(
        providers,
        new[] { Group(1), Group(2) },
        new Dictionary<long, double>(),
        Policy(RoutingMode.Economy),
        now);

    Assert(evaluation.Recommended?.Group.Id == 1, "Economy mode paid too much for the latency improvement.");
}

static void TestEconomyLatencyUtilityIsContinuous()
{
    var threshold = BalancedRoutingPolicy.EconomyLatencyDiminishingThresholdMs;
    var justBelow = RoutingEngine.ApplyLatencyDiminishingReturns(
        threshold - 1,
        RoutingMode.Economy);
    var atThreshold = RoutingEngine.ApplyLatencyDiminishingReturns(
        threshold,
        RoutingMode.Economy);
    var justAbove = RoutingEngine.ApplyLatencyDiminishingReturns(
        threshold + 1,
        RoutingMode.Economy);

    Assert(Math.Abs(atThreshold - threshold) < 1e-12,
        "Economy latency utility moved the threshold itself.");
    Assert(justBelow < atThreshold && atThreshold < justAbove,
        "Economy latency utility is not monotonic around the threshold.");
    Assert(Math.Abs(atThreshold - justBelow - BalancedRoutingPolicy.EconomyLatencyDiminishingFactor) < 1e-12,
        "Economy latency utility did not use the configured sub-threshold slope.");
}

static void TestEconomyModeCompressesSubThresholdSpeedGain()
{
    var now = DateTimeOffset.UtcNow;
    var evaluation = RoutingEngine.Evaluate(
        [
            Provider(1, 0.02, true, 0.99, now, latency: 3_000),
            Provider(2, 0.021, true, 0.99, now, latency: 1_000)
        ],
        [Group(1), Group(2)],
        new Dictionary<long, double>(),
        Policy(RoutingMode.Economy),
        now);

    Assert(evaluation.Recommended?.Group.Id == 1,
        "Economy mode still paid a premium for a sub-threshold speed gain after diminishing returns.");
}

static void TestNonEconomyLatencyUtilityRemainsRaw()
{
    var latency = 1_000d;
    Assert(RoutingEngine.ApplyLatencyDiminishingReturns(latency, RoutingMode.Balanced) == latency &&
           RoutingEngine.ApplyLatencyDiminishingReturns(latency, RoutingMode.Speed) == latency,
        "Balanced or Speed mode changed the raw latency utility.");
}

static void TestEconomyLatencyUtilityPenalizesSevereLatency()
{
    var threshold = BalancedRoutingPolicy.EconomySevereLatencyThresholdMs;
    var atThreshold = RoutingEngine.ApplyLatencyDiminishingReturns(
        threshold,
        RoutingMode.Economy);
    var justAbove = RoutingEngine.ApplyLatencyDiminishingReturns(
        threshold + 1,
        RoutingMode.Economy);
    var verySlow = RoutingEngine.ApplyLatencyDiminishingReturns(
        20_000,
        RoutingMode.Economy);

    Assert(atThreshold == threshold,
        "Economy severe latency threshold changed the effective latency at the boundary.");
    Assert(Math.Abs(justAbove - atThreshold - BalancedRoutingPolicy.EconomySevereLatencyFactor) < 1e-12,
        "Economy severe latency did not use the increased post-threshold slope.");
    Assert(verySlow > 20_000,
        "Economy severe latency did not reduce the effective score of a very slow candidate.");
}

static void TestBalancedModeRejectsCatastrophicCheapLatency()
{
    var now = DateTimeOffset.UtcNow;
    var evaluation = RoutingEngine.Evaluate(
        new[]
        {
            Provider(1, 0.03, true, 0.99, now, latency: 111_032),
            Provider(2, 0.05, true, 0.99, now, latency: 6_025)
        },
        new[] { Group(1), Group(2) },
        new Dictionary<long, double>(),
        Policy(RoutingMode.Balanced),
        now);

    Assert(evaluation.Recommended?.Group.Id == 2,
        "Balanced mode accepted a 94% latency regression for a 40% price reduction.");
}

static void TestBalancedModeBuysSpeedForModerateGap()
{
    var now = DateTimeOffset.UtcNow;
    var evaluation = RoutingEngine.Evaluate(
        new[]
        {
            Provider(1, 0.03, true, 0.99, now, latency: 6_051),
            Provider(2, 0.05, true, 0.99, now, latency: 1_891)
        },
        new[] { Group(1), Group(2) },
        new Dictionary<long, double>(),
        Policy(RoutingMode.Balanced),
        now);

    Assert(evaluation.Recommended?.Group.Id == 2,
        "Balanced mode did not buy a meaningful latency improvement.");
}

static void TestBalancedModeHoldsMarginalSpeedGain()
{
    var now = DateTimeOffset.UtcNow;
    var policy = Policy(RoutingMode.Balanced);
    var evaluation = RoutingEngine.Evaluate(
        new[]
        {
            Provider(1, 0.03, true, 0.99, now, latency: 1_000),
            Provider(2, 0.0301, true, 0.99, now, latency: 980)
        },
        new[] { Group(1), Group(2) },
        new Dictionary<long, double>(),
        policy,
        now);
    var result = RouteDecisionEngine.Decide(
        evaluation,
        new RouteState { CurrentGroupId = 1 },
        policy,
        now,
        observedCurrentGroupId: 1);

    Assert(!result.Decision.ShouldSwitch && result.Decision.Target?.Group.Id == 1,
        "Balanced mode switched for a marginal weighted advantage.");
}

static void TestBalancedModeEscapesExtremeLatency()
{
    var now = DateTimeOffset.UtcNow;
    var policy = Policy(RoutingMode.Balanced);
    var evaluation = RoutingEngine.Evaluate(
        new[]
        {
            Provider(1, 0.01, true, 0.99, now, latency: 120_000),
            Provider(2, 0.02, true, 0.99, now, latency: 9_000)
        },
        new[] { Group(1), Group(2) },
        new Dictionary<long, double>(),
        policy,
        now);
    var result = RouteDecisionEngine.Decide(
        evaluation,
        new RouteState { CurrentGroupId = 1 },
        policy,
        now,
        observedCurrentGroupId: 1);

    Assert(result.Decision.ShouldSwitch && result.Decision.Target?.Group.Id == 2,
        "Balanced mode kept an extreme-latency route despite an acceptable double-price route.");
}

static void TestBalancedModeRejectsWeakLatencyValue()
{
    var now = DateTimeOffset.UtcNow;
    var evaluation = RoutingEngine.Evaluate(
        new[]
        {
            Provider(1, 0.02, true, 0.99, now, latency: 1_000),
            Provider(2, 0.03, true, 0.99, now, latency: 900)
        },
        new[] { Group(1), Group(2) },
        new Dictionary<long, double>(),
        Policy(RoutingMode.Balanced),
        now);

    Assert(evaluation.Recommended?.Group.Id == 1,
        "Balanced mode paid a 50% premium for only a 10% latency improvement.");
}

static void TestSpeedModeAcceptsLargerPremium()
{
    var now = DateTimeOffset.UtcNow;
    var evaluation = RoutingEngine.Evaluate(
        new[]
        {
            Provider(1, 0.02, true, 0.99, now, latency: 10_000),
            Provider(2, 0.04, true, 0.99, now, latency: 2_000)
        },
        new[] { Group(1), Group(2) },
        new Dictionary<long, double>(),
        Policy(RoutingMode.Speed),
        now);

    Assert(evaluation.Recommended?.Group.Id == 2,
        "Speed mode rejected a large latency gain despite its lower price penalty.");
}

static void TestMissingLatencyRanksLast()
{
    var now = DateTimeOffset.UtcNow;
    var providers = new[]
    {
        Provider(1, 0.02, true, 0.99, now, latency: null),
        Provider(2, 0.02, true, 0.99, now, latency: 2_000)
    };
    var evaluation = RoutingEngine.Evaluate(
        providers,
        new[] { Group(1), Group(2) },
        new Dictionary<long, double>(),
        Policy(RoutingMode.Balanced),
        now);

    Assert(evaluation.Recommended?.Group.Id == 2, "A missing latency value outranked a measured latency.");
}

static void TestZeroMultiplierWindow()
{
    var now = DateTimeOffset.UtcNow;
    var providers = new[]
    {
        Provider(1, 0, true, 0.99, now, latency: 8_000),
        Provider(2, 0.001, true, 0.99, now, latency: 100)
    };
    var evaluation = RoutingEngine.Evaluate(
        providers,
        new[] { Group(1), Group(2) },
        new Dictionary<long, double>(),
        Policy(RoutingMode.Speed),
        now);

    Assert(evaluation.TradeoffCandidates.Count == 1, "A paid route competed with a zero-cost route.");
    Assert(evaluation.Recommended?.Group.Id == 1, "Zero multiplier route was not retained.");
}

static void TestWeightedSpeedWinnerSwitchesImmediately()
{
    var now = DateTimeOffset.UtcNow;
    var evaluation = EvaluationForSwitch(now, currentRate: 0.02, targetRate: 0.021);
    var policy = Policy(RoutingMode.Balanced);
    var state = new RouteState { CurrentGroupId = 1 };

    var result = RouteDecisionEngine.Decide(evaluation, state, policy, now, observedCurrentGroupId: 1);

    Assert(result.Decision.ShouldSwitch &&
        result.Decision.Reason == RouteDecisionReason.FasterForWeightedTradeoff,
        "A clear weighted speed winner did not switch on the first evaluation.");
}

static void TestCloseFasterScoreKeepsCurrentGroup()
{
    var now = DateTimeOffset.UtcNow;
    var evaluation = RoutingEngine.Evaluate(
        new[]
        {
            Provider(1, 0.02, true, 0.99, now, latency: 1_000),
            Provider(2, 0.02, true, 0.99, now, latency: 980)
        },
        new[] { Group(1), Group(2) },
        new Dictionary<long, double>(),
        Policy(RoutingMode.Balanced),
        now);
    Assert(evaluation.Recommended?.Group.Id == 2,
        "Test setup did not produce a slightly better faster route.");

    var result = RouteDecisionEngine.Decide(
        evaluation,
        new RouteState { CurrentGroupId = 1 },
        Policy(RoutingMode.Balanced),
        now,
        observedCurrentGroupId: 1);

    Assert(!result.Decision.ShouldSwitch && result.Decision.Target?.Group.Id == 1,
        "A tiny weighted speed advantage replaced the current group.");
    Assert(result.Decision.Reason == RouteDecisionReason.ScoreAdvantageTooSmall,
        "A held close score did not report the stability reason.");
}

static void TestCloseCheaperScoreKeepsCurrentGroup()
{
    var now = DateTimeOffset.UtcNow;
    var evaluation = RoutingEngine.Evaluate(
        new[]
        {
            Provider(1, 0.0201, true, 0.99, now, latency: 999),
            Provider(2, 0.02, true, 0.99, now, latency: 1_000)
        },
        new[] { Group(1), Group(2) },
        new Dictionary<long, double>(),
        Policy(RoutingMode.Balanced),
        now);
    Assert(evaluation.Recommended?.Group.Id == 2,
        "Test setup did not produce a slightly better cheaper route.");

    var result = RouteDecisionEngine.Decide(
        evaluation,
        new RouteState { CurrentGroupId = 1 },
        Policy(RoutingMode.Balanced),
        now,
        observedCurrentGroupId: 1);

    Assert(!result.Decision.ShouldSwitch && result.Decision.Target?.Group.Id == 1,
        "A tiny weighted price advantage replaced the current group.");
    Assert(result.Decision.Reason == RouteDecisionReason.ScoreAdvantageTooSmall,
        "A held close price score did not report the stability reason.");
}

static void TestMeaningfulScoreAdvantageStillSwitches()
{
    var now = DateTimeOffset.UtcNow;
    var evaluation = RoutingEngine.Evaluate(
        new[]
        {
            Provider(1, 0.02, true, 0.99, now, latency: 1_000),
            Provider(2, 0.02, true, 0.99, now, latency: 400)
        },
        new[] { Group(1), Group(2) },
        new Dictionary<long, double>(),
        Policy(RoutingMode.Balanced),
        now);
    Assert(evaluation.Recommended?.Group.Id == 2,
        "Test setup did not produce a meaningful faster route.");

    var result = RouteDecisionEngine.Decide(
        evaluation,
        new RouteState { CurrentGroupId = 1 },
        Policy(RoutingMode.Balanced),
        now,
        observedCurrentGroupId: 1);

    Assert(result.Decision.ShouldSwitch && result.Decision.Target?.Group.Id == 2,
        "A score advantage above the stability threshold was blocked.");
}

static void TestUnknownCurrentLatencyUsesMeasuredRoute()
{
    var now = DateTimeOffset.UtcNow;
    var evaluation = RoutingEngine.Evaluate(
        new[]
        {
            Provider(1, 0.02, true, 0.99, now, latency: null),
            Provider(2, 0.021, true, 0.99, now, latency: 1_000)
        },
        new[] { Group(1), Group(2) },
        new Dictionary<long, double>(),
        Policy(RoutingMode.Balanced),
        now);
    var state = new RouteState { CurrentGroupId = 1 };

    var result = RouteDecisionEngine.Decide(
        evaluation,
        state,
        Policy(RoutingMode.Balanced),
        now,
        observedCurrentGroupId: 1);

    Assert(result.Decision.ShouldSwitch && result.Decision.Target?.Group.Id == 2,
        "A measured route did not replace a route with unknown latency.");
    Assert(result.Decision.LatencyImprovementPercent is null, "Unknown latency produced a numeric improvement.");
}

static void TestPriceWinnerSwitchesImmediately()
{
    var now = DateTimeOffset.UtcNow;
    var evaluation = EvaluationForSwitch(now, currentRate: 0.03, targetRate: 0.02);
    var state = new RouteState { CurrentGroupId = 1 };

    var result = RouteDecisionEngine.Decide(
        evaluation,
        state,
        Policy(RoutingMode.Balanced),
        now,
        observedCurrentGroupId: 1);

    Assert(result.Decision.ShouldSwitch && result.Decision.Reason == RouteDecisionReason.BetterPrice,
        "A lower-price weighted winner did not switch immediately.");
}

static void TestPlainHttpIsRejected()
{
    var rejected = false;
    try
    {
        using var unused = new AIHubClient("http://example.test");
    }
    catch (ArgumentException)
    {
        rejected = true;
    }

    Assert(rejected, "Plain HTTP was accepted for a non-loopback host.");
    using var loopback = new AIHubClient("http://127.0.0.1:8080", allowInsecureLoopback: true);
}

static void TestCredentialPersistenceDefaultsToEnabled()
{
    var directory = Path.Combine(Path.GetTempPath(), "AIHubRouter.Tests", Guid.NewGuid().ToString("N"));
    try
    {
        Assert(new PersistentAppSettings().PersistCredentials,
            "New settings did not default credential persistence to enabled.");

        var protector = new UnavailableCredentialProtector("unit test unavailable protector");
        var store = new AppSettingsStore(directory, protector);
        Assert(store.Load().Settings.PersistCredentials,
            "Missing settings did not default credential persistence to enabled.");

        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "settings.json"),
            "{\"baseUrl\":\"https://example.test\"}");
        Assert(store.Load().Settings.PersistCredentials,
            "Settings without persistCredentials did not use the new default.");

        File.WriteAllText(
            Path.Combine(directory, "settings.json"),
            "{\"persistCredentials\":false}");
        Assert(!store.Load().Settings.PersistCredentials,
            "An explicit legacy false persistence choice was not preserved.");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void TestAesSettingsRoundtrip()
{
    var directory = Path.Combine(Path.GetTempPath(), "AIHubRouter.Tests", Guid.NewGuid().ToString("N"));
    var key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
    try
    {
        using var protector = new AesGcmCredentialProtector(key);
        var store = new AppSettingsStore(directory, protector);
        var credentials = new PersistentCredentials
        {
            Email = "user@example.test",
            Password = "secret-password",
            BearerToken = "secret-token",
            RefreshToken = "secret-refresh-token",
            AccessTokenExpiresAt = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero),
            Cookie = "session=secret-cookie",
            UserAgent = "secret-user-agent"
        };
        store.Save(
            new PersistentAppSettings
            {
                PersistCredentials = true,
                ThemeMode = AppThemeMode.Dark
            },
            credentials);

        var encrypted = File.ReadAllBytes(Path.Combine(directory, "credentials.dat"));
        var encryptedText = Encoding.UTF8.GetString(encrypted);
        Assert(!encryptedText.Contains(credentials.Email, StringComparison.Ordinal), "AES file contains email plaintext.");
        Assert(!encryptedText.Contains(credentials.Password, StringComparison.Ordinal), "AES file contains password plaintext.");
        Assert(!encryptedText.Contains(credentials.BearerToken, StringComparison.Ordinal), "AES file contains token plaintext.");
        Assert(!encryptedText.Contains(credentials.RefreshToken, StringComparison.Ordinal), "AES file contains refresh token plaintext.");
        Assert(!encryptedText.Contains(credentials.Cookie, StringComparison.Ordinal), "AES file contains cookie plaintext.");
        Assert(!encryptedText.Contains(credentials.UserAgent, StringComparison.Ordinal), "AES file contains user-agent plaintext.");
        var loaded = store.Load();
        Assert(loaded.Credentials?.Password == credentials.Password, "AES password did not roundtrip.");
        Assert(loaded.Credentials?.BearerToken == credentials.BearerToken, "AES token did not roundtrip.");
        Assert(loaded.Credentials?.Email == credentials.Email, "AES email did not roundtrip.");
        Assert(loaded.Credentials?.RefreshToken == credentials.RefreshToken, "AES refresh token did not roundtrip.");
        Assert(loaded.Credentials?.AccessTokenExpiresAt == credentials.AccessTokenExpiresAt, "AES expiry did not roundtrip.");
        Assert(loaded.Credentials?.Cookie == credentials.Cookie, "AES cookie did not roundtrip.");
        Assert(loaded.Credentials?.UserAgent == credentials.UserAgent, "AES user-agent did not roundtrip.");
        Assert(loaded.Settings.ThemeMode == AppThemeMode.Dark, "Theme mode did not roundtrip.");
    }
    finally
    {
        Array.Clear(key);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void TestProfileSettingsChangeSignal()
{
    var directory = Path.Combine(Path.GetTempPath(), "AIHubRouter.Tests", Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(directory);
        using var monitor = new ProfileFileChangeMonitor(directory);
        var temporaryPath = Path.Combine(directory, "settings.json.tmp");
        var settingsPath = Path.Combine(directory, "settings.json");
        File.WriteAllText(temporaryPath, "{}");
        File.Move(temporaryPath, settingsPath);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        monitor.WaitForChangeAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void TestDryRunNeverUpdatesKey()
{
    var now = DateTimeOffset.UtcNow;
    var api = new StubAIHubApiClient(now);
    var settings = new PersistentAppSettings
    {
        KeySelectionInitialized = true,
        SelectedKeyIds = [10]
    };
    using var service = new RoutingService(
        settings,
        new PersistentCredentials { BearerToken = "test-token" },
        new MemoryRouteStateStore(),
        new StubAIHubClientFactory(api),
        utcNow: () => now);

    var result = service.RunOnceAsync(dryRun: true).GetAwaiter().GetResult();
    Assert(result.Decision.ShouldSwitch, "Dry run did not calculate a switch.");
    Assert(api.UpdateCalls == 0, "Dry run called UpdateKeyGroupAsync.");
}

static void TestProviderSeriesCacheAndFailureFallback()
{
    var clock = DateTimeOffset.UtcNow;
    var api = new StubAIHubApiClient(
        clock,
        providerSeries: call =>
        {
            if (call == 2)
            {
                throw new TaskCanceledException("request timeout");
            }

            if (call >= 3)
            {
                throw new HttpRequestException("sensitive upstream detail");
            }

            return new ProviderSeriesPage(
                clock,
                "6h",
                new Dictionary<long, ProviderSeriesMetrics>
                {
                    [2] = new(2, 1, 500, 500, 20, 20, clock)
                });
        });
    var settings = new PersistentAppSettings
    {
        KeySelectionInitialized = true,
        SelectedKeyIds = [10],
        ProviderSeriesCacheSeconds = 60
    };
    using var service = new RoutingService(
        settings,
        new PersistentCredentials { BearerToken = "test-token" },
        new MemoryRouteStateStore(),
        new StubAIHubClientFactory(api),
        utcNow: () => clock);

    var live = service.RunOnceAsync(dryRun: true).GetAwaiter().GetResult();
    var callsAfterLive = api.ProviderSeriesCalls;
    var cached = service.RunOnceAsync(dryRun: true).GetAwaiter().GetResult();
    var callsAfterCache = api.ProviderSeriesCalls;
    var forcedFallback = service.RunOnceAsync(
        dryRun: true,
        forceAccountRefresh: true).GetAwaiter().GetResult();
    clock = clock.AddSeconds(61);
    var expiredFallback = service.RunOnceAsync(dryRun: true).GetAwaiter().GetResult();

    Assert(live.ProviderSeriesStatus.Available && !live.ProviderSeriesStatus.FromCache,
        "Initial provider series request was not reported as live.");
    Assert(cached.ProviderSeriesStatus.FromCache &&
           callsAfterLive == 1 &&
           callsAfterCache == callsAfterLive &&
           api.ProviderSeriesCalls == 3,
        "A normal routing cycle did not use the provider series cache.");
    Assert(forcedFallback.ProviderSeriesStatus.Available &&
           forcedFallback.ProviderSeriesStatus.FromCache &&
           forcedFallback.ProviderSeriesStatus.IsDegraded,
        "A failed forced refresh did not preserve the fresh cache.");
    Assert(!expiredFallback.ProviderSeriesStatus.Available &&
           !expiredFallback.ProviderSeriesStatus.Message.Contains(
               "sensitive upstream detail",
               StringComparison.Ordinal),
        "An expired cache failure did not safely fall back to the base score.");
}

static void TestProviderSeriesCacheRejectsAgeExpiredPage()
{
    var clock = DateTimeOffset.UtcNow;
    var pageTime = clock.AddMinutes(-14);
    var api = new StubAIHubApiClient(
        clock,
        providerSeries: call => call == 1
            ? new ProviderSeriesPage(
                pageTime,
                "6h",
                new Dictionary<long, ProviderSeriesMetrics>
                {
                    [2] = new(2, 1, 500, 500, 20, 20, pageTime)
                })
            : throw new TaskCanceledException("request timeout"));
    var settings = new PersistentAppSettings
    {
        KeySelectionInitialized = true,
        SelectedKeyIds = [10],
        ProviderSeriesCacheSeconds = 3600
    };
    using var service = new RoutingService(
        settings,
        new PersistentCredentials { BearerToken = "test-token" },
        new MemoryRouteStateStore(),
        new StubAIHubClientFactory(api),
        utcNow: () => clock);

    var live = service.RunOnceAsync(dryRun: true).GetAwaiter().GetResult();
    clock = clock.AddMinutes(2);
    var stale = service.RunOnceAsync(dryRun: true).GetAwaiter().GetResult();

    Assert(live.ProviderSeriesStatus.Available &&
           !stale.ProviderSeriesStatus.Available,
        "A cache entry past maximum data age remained available inside its TTL.");
}

static void TestProviderSeriesCallerCancellationPropagates()
{
    var now = DateTimeOffset.UtcNow;
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var api = new StubAIHubApiClient(
        now,
        providerSeries: _ => throw new OperationCanceledException(cancellation.Token));
    var settings = new PersistentAppSettings();
    var cache = new ProviderSeriesCache(settings);
    var propagated = false;

    try
    {
        cache.LoadAsync(
                api,
                settings.CreatePolicy(),
                now,
                forceRefresh: false,
                cancellation.Token)
            .GetAwaiter()
            .GetResult();
    }
    catch (OperationCanceledException)
    {
        propagated = true;
    }

    Assert(propagated, "Caller cancellation was mistaken for a provider series timeout.");
}

static void TestAutomaticRouteHonorsExplicitMultiKeySelection()
{
    var now = DateTimeOffset.UtcNow;
    var api = new StubAIHubApiClient(
        now,
        keys:
        [
            new ApiKeyInfo { Id = 10, Name = "First", Status = "active", GroupId = 1 },
            new ApiKeyInfo { Id = 11, Name = "Second", Status = "active", GroupId = 1 }
        ]);
    var settings = new PersistentAppSettings
    {
        KeySelectionInitialized = true,
        SelectedKeyIds = [11]
    };
    using var service = new RoutingService(
        settings,
        new PersistentCredentials { BearerToken = "test-token" },
        new MemoryRouteStateStore(),
        new StubAIHubClientFactory(api),
        utcNow: () => now);

    var result = service.RunOnceAsync(
        selectedKeyIds: new long[] { 10, 11 }).GetAwaiter().GetResult();

    Assert(api.UpdateCalls == 2, "Automatic route did not update every explicitly selected Key.");
    Assert(result.SelectedKeyIds.Order().SequenceEqual(new long[] { 10, 11 }),
        "Automatic route did not return every explicitly selected Key.");
    Assert(result.Keys.Where(key => key.Id is 10 or 11).All(key => key.GroupId == 2),
        "Automatic route did not return the updated group for every explicitly selected Key.");
}

static void TestLunaRouteFiltersFailedModelHealthGroups()
{
    var now = DateTimeOffset.UtcNow;
    var api = new StubAIHubApiClient(
        now,
        keys:
        [
            new ApiKeyInfo { Id = 10, Name = "主 Key", Status = "active", GroupId = 1 },
            new ApiKeyInfo { Id = 11, Name = "Luna Key", Status = "active", GroupId = 1 }
        ],
        providerCacheHitRates: _ => new ProviderCacheHitRatePage(
            now,
            new Dictionary<long, double> { [2] = 0.80 })
        {
            ModelHealthByGroup = new Dictionary<long, IReadOnlyDictionary<string, string>>
            {
                [2] = new Dictionary<string, string> { ["luna"] = "failed" }
            }
        });
    var settings = new PersistentAppSettings
    {
        KeySelectionInitialized = true,
        SelectedKeyIds = [10],
        LunaSelectedKeyIds = [11],
        ProviderSeriesWeight = 0
    };
    using var service = new RoutingService(
        settings,
        new PersistentCredentials { BearerToken = "test-token" },
        new MemoryRouteStateStore(),
        new StubAIHubClientFactory(api),
        utcNow: () => now);

    var result = service.RunOnceAsync(forceAccountRefresh: true).GetAwaiter().GetResult();

    Assert(result.LunaRoute is { HealthAvailable: true, FilteredGroupCount: 1 },
        "Luna route did not expose the filtered failed group.");
    Assert(result.LunaRoute?.Decision?.Target is null,
        "Luna route selected a group explicitly marked failed for Luna.");
    Assert(api.ProviderCacheHitRateCalls == 1,
        "Luna health was not loaded when provider series scoring was disabled.");
    Assert(api.UpdateCalls == 1 && result.KeyResults.Count == 1,
        "Luna filtering should leave the main route update independent and avoid a Luna PUT.");
}

static void TestLunaHealthFailureDoesNotBlockPrimaryRoute()
{
    var now = DateTimeOffset.UtcNow;
    var api = new StubAIHubApiClient(
        now,
        keys:
        [
            new ApiKeyInfo { Id = 10, Name = "主 Key", Status = "active", GroupId = 1 },
            new ApiKeyInfo { Id = 11, Name = "Luna Key", Status = "active", GroupId = 1 }
        ],
        providerCacheHitRates: _ => throw new HttpRequestException("upstream detail"));
    var settings = new PersistentAppSettings
    {
        KeySelectionInitialized = true,
        SelectedKeyIds = [10],
        LunaSelectedKeyIds = [11]
    };
    using var service = new RoutingService(
        settings,
        new PersistentCredentials { BearerToken = "test-token" },
        new MemoryRouteStateStore(),
        new StubAIHubClientFactory(api),
        utcNow: () => now);

    var result = service.RunOnceAsync(forceAccountRefresh: true).GetAwaiter().GetResult();

    Assert(result.KeyResults.Count == 1 && api.UpdateCalls == 1,
        "Primary routing should continue after Luna health loading fails.");
    Assert(result.LunaRoute is { HealthAvailable: false } &&
           result.LunaRoute.KeyResults.Count == 0,
           "Luna health failure should stop Luna writes and expose a degraded result.");
}

static void TestLunaHealthLoadsAfterPrimaryOnlyCycle()
{
    var now = DateTimeOffset.UtcNow;
    var api = new StubAIHubApiClient(
        now,
        keys:
        [
            new ApiKeyInfo { Id = 10, Name = "主 Key", Status = "active", GroupId = 2 },
            new ApiKeyInfo { Id = 11, Name = "Luna Key", Status = "active", GroupId = 1 }
        ],
        providerCacheHitRates: _ => new ProviderCacheHitRatePage(
            now,
            new Dictionary<long, double>())
        {
            ModelHealthByGroup = new Dictionary<long, IReadOnlyDictionary<string, string>>
            {
                [2] = new Dictionary<string, string> { ["luna"] = "healthy" }
            }
        });
    var settings = new PersistentAppSettings
    {
        KeySelectionInitialized = true,
        SelectedKeyIds = [10],
        ProviderSeriesWeight = 0
    };
    using var service = new RoutingService(
        settings,
        new PersistentCredentials { BearerToken = "test-token" },
        new MemoryRouteStateStore(),
        new StubAIHubClientFactory(api),
        utcNow: () => now);

    service.RunOnceAsync().GetAwaiter().GetResult();
    Assert(api.ProviderCacheHitRateCalls == 0,
        "A primary-only cycle with zero provider weight should not fetch health data.");

    var result = service.RunOnceAsync(selectedLunaKeyIds: new long[] { 11 })
        .GetAwaiter()
        .GetResult();

    Assert(api.ProviderCacheHitRateCalls == 1,
        "Enabling Luna on an existing service should refresh the missing health snapshot.");
    Assert(result.LunaRoute is { HealthAvailable: true } && api.UpdateCalls == 1,
        "Luna did not route after its health data was loaded on demand.");
}

static void TestOverlappingMainAndLunaKeysAreRejected()
{
    var now = DateTimeOffset.UtcNow;
    var api = new StubAIHubApiClient(now);
    var settings = new PersistentAppSettings
    {
        KeySelectionInitialized = true,
        SelectedKeyIds = [10],
        LunaSelectedKeyIds = [10]
    };
    using var service = new RoutingService(
        settings,
        new PersistentCredentials { BearerToken = "test-token" },
        new MemoryRouteStateStore(),
        new StubAIHubClientFactory(api),
        utcNow: () => now);

    var rejected = false;
    try
    {
        service.RunOnceAsync(forceAccountRefresh: true).GetAwaiter().GetResult();
    }
    catch (InvalidOperationException exception)
    {
        rejected = exception.Message.Contains("不能选择同一 Key", StringComparison.Ordinal);
    }

    Assert(rejected, "Overlapping main and Luna Key selections were not rejected.");
    Assert(api.UpdateCalls == 0, "Overlapping Key validation happened after a PUT.");
}

static void TestManualRouteUpdatesSelectedKeysAndState()
{
    var now = DateTimeOffset.UtcNow;
    var api = new StubAIHubApiClient(now);
    var stateStore = new MemoryRouteStateStore();
    var settings = new PersistentAppSettings
    {
        KeySelectionInitialized = true,
        SelectedKeyIds = [10]
    };
    using var service = new RoutingService(
        settings,
        new PersistentCredentials { BearerToken = "test-token" },
        stateStore,
        new StubAIHubClientFactory(api),
        utcNow: () => now);

    var result = service.RouteManuallyAsync(2).GetAwaiter().GetResult();

    Assert(api.UpdateCalls == 1, "Manual route did not update the selected Key.");
    Assert(result.ChangedKeyCount == 1 && result.FailedKeyCount == 0,
        "Manual route returned incorrect Key result counts.");
    Assert(result.Keys.Single(key => key.Id == 10).GroupId == 2,
        "Manual route did not return the updated Key group.");
    Assert(stateStore.Current.CurrentGroupId == 2,
        "Manual route did not persist the selected group as current.");
}

static void TestManualRouteHonorsExplicitMultiKeySelection()
{
    var now = DateTimeOffset.UtcNow;
    var api = new StubAIHubApiClient(
        now,
        keys:
        [
            new ApiKeyInfo { Id = 10, Name = "First", Status = "active", GroupId = 1 },
            new ApiKeyInfo { Id = 11, Name = "Second", Status = "active", GroupId = 1 }
        ]);
    var settings = new PersistentAppSettings
    {
        KeySelectionInitialized = true,
        SelectedKeyIds = [11]
    };
    using var service = new RoutingService(
        settings,
        new PersistentCredentials { BearerToken = "test-token" },
        new MemoryRouteStateStore(),
        new StubAIHubClientFactory(api),
        utcNow: () => now);

    var result = service.RouteManuallyAsync(
        2,
        selectedKeyIds: new long[] { 10, 11 }).GetAwaiter().GetResult();

    Assert(api.UpdateCalls == 2, "Manual route did not update every explicitly selected Key.");
    Assert(result.SelectedKeyIds.Order().SequenceEqual(new long[] { 10, 11 }),
        "Manual route did not return every explicitly selected Key.");
    Assert(result.Keys.Where(key => key.Id is 10 or 11).All(key => key.GroupId == 2),
        "Manual route did not return the updated group for every explicitly selected Key.");
}

static void TestManualRouteRejectsBlacklistedGroup()
{
    var now = DateTimeOffset.UtcNow;
    var api = new StubAIHubApiClient(now);
    var stateStore = new MemoryRouteStateStore(new RouteState { CurrentGroupId = 1 });
    var settings = new PersistentAppSettings
    {
        KeySelectionInitialized = true,
        SelectedKeyIds = [10],
        BlacklistedGroupIds = [2]
    };
    using var service = new RoutingService(
        settings,
        new PersistentCredentials { BearerToken = "test-token" },
        stateStore,
        new StubAIHubClientFactory(api),
        utcNow: () => now);

    var rejected = false;
    try
    {
        service.RouteManuallyAsync(2).GetAwaiter().GetResult();
    }
    catch (InvalidOperationException exception) when (exception.Message == "所选分组已加入黑名单。")
    {
        rejected = true;
    }

    Assert(rejected, "Manual route accepted a blacklisted group.");
    Assert(api.UpdateCalls == 0, "Manual route updated a Key for a blacklisted group.");
    Assert(stateStore.Current.CurrentGroupId == 1,
        "Manual route changed the persisted state for a blacklisted group.");
}

static void TestManualRouteRejectsOutOfRangeGroup()
{
    var now = DateTimeOffset.UtcNow;
    var api = new StubAIHubApiClient(now);
    var stateStore = new MemoryRouteStateStore(new RouteState { CurrentGroupId = 1 });
    var settings = new PersistentAppSettings
    {
        KeySelectionInitialized = true,
        SelectedKeyIds = [10],
        MaximumPriceMultiplier = 0.01
    };
    using var service = new RoutingService(
        settings,
        new PersistentCredentials { BearerToken = "test-token" },
        stateStore,
        new StubAIHubClientFactory(api),
        utcNow: () => now);

    var rejected = false;
    try
    {
        service.RouteManuallyAsync(2).GetAwaiter().GetResult();
    }
    catch (InvalidOperationException exception) when (exception.Message.StartsWith("所选分组不在允许价格范围", StringComparison.Ordinal))
    {
        rejected = true;
    }

    Assert(rejected, "Manual route accepted an out-of-range group.");
    Assert(api.UpdateCalls == 0, "Manual route updated a Key for an out-of-range group.");
    Assert(stateStore.Current.CurrentGroupId == 1,
        "Manual route changed the persisted state for an out-of-range group.");
}

static void TestManualRouteClearsStateAfterTerminalAuthenticationFailure()
{
    var now = DateTimeOffset.UtcNow;
    var api = new StubAIHubApiClient(
        now,
        keys:
        [
            new ApiKeyInfo { Id = 10, Name = "First", Status = "active", GroupId = 1 },
            new ApiKeyInfo { Id = 11, Name = "Second", Status = "active", GroupId = 1 }
        ],
        updateFailure: (call, _, _) => call == 2
            ? new AIHubApiException("token expired", HttpStatusCode.Unauthorized)
            : null);
    var stateStore = new MemoryRouteStateStore(new RouteState { CurrentGroupId = 1 });
    var settings = new PersistentAppSettings
    {
        KeySelectionInitialized = true,
        SelectedKeyIds = [10, 11]
    };
    using var service = new RoutingService(
        settings,
        new PersistentCredentials { BearerToken = "test-token" },
        stateStore,
        new StubAIHubClientFactory(api),
        utcNow: () => now);

    var rejected = false;
    try
    {
        service.RouteManuallyAsync(2).GetAwaiter().GetResult();
    }
    catch (AIHubApiException exception) when (exception.IsAuthenticationFailure)
    {
        rejected = true;
    }

    Assert(rejected, "Terminal authentication failure was not propagated.");
    Assert(api.UpdateCalls == 2, "Manual route did not attempt the expected Key updates.");
    Assert(stateStore.Current.CurrentGroupId is null,
        "Partial manual route kept a stale current group after authentication failure.");
}

static void TestManualRoutePreservesChangesAcrossAuthenticationRetry()
{
    var now = DateTimeOffset.UtcNow;
    var api = new StubAIHubApiClient(
        now,
        keys:
        [
            new ApiKeyInfo { Id = 10, Name = "First", Status = "active", GroupId = 1 },
            new ApiKeyInfo { Id = 11, Name = "Second", Status = "active", GroupId = 1 }
        ],
        updateFailure: (call, _, _) => call == 2
            ? new AIHubApiException("token expired", HttpStatusCode.Unauthorized)
            : null,
        supportsRefresh: true);
    var stateStore = new MemoryRouteStateStore(new RouteState { CurrentGroupId = 1 });
    var settings = new PersistentAppSettings
    {
        KeySelectionInitialized = true,
        SelectedKeyIds = [10, 11]
    };
    using var service = new RoutingService(
        settings,
        new PersistentCredentials
        {
            BearerToken = "test-token",
            RefreshToken = "refresh-token",
            AccessTokenExpiresAt = now.AddHours(1)
        },
        stateStore,
        new StubAIHubClientFactory(api),
        utcNow: () => now);

    var result = service.RouteManuallyAsync(2).GetAwaiter().GetResult();

    Assert(api.UpdateCalls == 3, "Manual route did not retry only the failed Key.");
    Assert(result.ChangedKeyCount == 2 && result.FailedKeyCount == 0,
        "Manual route lost changes completed before authentication retry.");
    Assert(stateStore.Current.CurrentGroupId == 2,
        "Manual route did not persist the target group after authentication retry.");
}

static void TestUnavailableCredentialStorageIsAtomic()
{
    var directory = Path.Combine(Path.GetTempPath(), "AIHubRouter.Tests", Guid.NewGuid().ToString("N"));
    try
    {
        var store = new AppSettingsStore(
            directory,
            new UnavailableCredentialProtector("unit test unavailable protector"));
        var rejected = false;
        try
        {
            store.Save(
                new PersistentAppSettings { PersistCredentials = true },
                new PersistentCredentials { BearerToken = "must-not-be-written" });
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        Assert(rejected, "Unavailable credential persistence was accepted.");
        Assert(!File.Exists(Path.Combine(directory, "settings.json")),
            "Settings enabled persistence before credential protection was validated.");
        Assert(!File.Exists(Path.Combine(directory, "credentials.dat")),
            "Credentials were written without an available protector.");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void TestLegacyHardGateSettingsAreIgnored()
{
    var directory = Path.Combine(Path.GetTempPath(), "AIHubRouter.Tests", Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "settings.json"),
            """
            {
              "requiredConfirmations": 2,
              "minimumDwellSeconds": 300
            }
            """);
        var store = new AppSettingsStore(
            directory,
            new UnavailableCredentialProtector("unit test unavailable protector"));

        var settings = store.Load().Settings;
        var policy = settings.CreatePolicy();
        Assert(Math.Abs(policy.PriceWeight - 0.50) < 0.0001,
            "Legacy settings changed the balanced price weight.");
        Assert(Math.Abs(policy.LatencyWeight - 0.50) < 0.0001,
            "Legacy settings changed the balanced latency weight.");
        Assert(Math.Abs(policy.MinimumScoreAdvantageToSwitch - 0.10) < 0.0001,
            "Legacy settings changed the score hysteresis threshold.");

        var economy = policy with { Mode = RoutingMode.Economy };
        Assert(Math.Abs(economy.PriceWeight - 0.90) < 0.0001 &&
               Math.Abs(economy.LatencyWeight - 0.10) < 0.0001,
            "Economy mode weights are not 90/10.");

        var speed = policy with { Mode = RoutingMode.Speed };
        Assert(Math.Abs(speed.PriceWeight - 0.10) < 0.0001 &&
               Math.Abs(speed.LatencyWeight - 0.90) < 0.0001,
            "Speed mode weights are not 10/90.");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void TestCredentialProtectionFailurePreservesPreviousFiles()
{
    var directory = Path.Combine(Path.GetTempPath(), "AIHubRouter.Tests", Guid.NewGuid().ToString("N"));
    var key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
    try
    {
        using var protector = new AesGcmCredentialProtector(key);
        var store = new AppSettingsStore(directory, protector);
        store.Save(
            new PersistentAppSettings { BaseUrl = "https://old.example.test" },
            new PersistentCredentials { BearerToken = "old-token" });
        var oldSettings = File.ReadAllBytes(Path.Combine(directory, "settings.json"));
        var oldCredentials = File.ReadAllBytes(Path.Combine(directory, "credentials.dat"));

        var failingStore = new AppSettingsStore(directory, new ThrowingCredentialProtector());
        var rejected = false;
        try
        {
            failingStore.Save(
                new PersistentAppSettings { BaseUrl = "https://new.example.test" },
                new PersistentCredentials { BearerToken = "new-token" });
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        Assert(rejected, "A protector failure was not reported.");
        Assert(oldSettings.SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "settings.json"))),
            "Settings changed after credential protection failed.");
        Assert(oldCredentials.SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "credentials.dat"))),
            "Credentials changed after credential protection failed.");
        Assert(!Directory.EnumerateFiles(directory, "*.tmp").Any(),
            "A temporary persistence file remained after protection failed.");
    }
    finally
    {
        Array.Clear(key);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void TestCredentialCommitFailureRollsBackSettings()
{
    var directory = Path.Combine(Path.GetTempPath(), "AIHubRouter.Tests", Guid.NewGuid().ToString("N"));
    var key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
    try
    {
        using var protector = new AesGcmCredentialProtector(key);
        var store = new AppSettingsStore(directory, protector);
        store.Save(
            new PersistentAppSettings { BaseUrl = "https://old.example.test" },
            new PersistentCredentials { BearerToken = "old-token" });
        var oldSettings = File.ReadAllBytes(Path.Combine(directory, "settings.json"));
        var oldCredentials = File.ReadAllBytes(Path.Combine(directory, "credentials.dat"));

        Directory.Delete(Path.Combine(directory, "credentials.dat"));
        Directory.CreateDirectory(Path.Combine(directory, "credentials.dat"));
        var rejected = false;
        try
        {
            store.Save(
                new PersistentAppSettings { BaseUrl = "https://new.example.test" },
                new PersistentCredentials { BearerToken = "new-token" });
        }
        catch (IOException)
        {
            rejected = true;
        }

        Assert(rejected, "A credential file commit failure was not reported.");
        Assert(oldSettings.SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "settings.json"))),
            "Settings changed after credential file commit failed.");
        Assert(Directory.Exists(Path.Combine(directory, "credentials.dat")),
            "The blocking credential path was unexpectedly removed.");
        Assert(!Directory.EnumerateFiles(directory, "*.tmp").Any(),
            "A temporary persistence file remained after commit rollback.");

        Directory.Delete(Path.Combine(directory, "credentials.dat"));
        File.WriteAllBytes(Path.Combine(directory, "credentials.dat"), oldCredentials);
    }
    finally
    {
        Array.Clear(key);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void TestPendingPersistenceTransactionRecoversOnLoad()
{
    var directory = Path.Combine(Path.GetTempPath(), "AIHubRouter.Tests", Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(directory);
        var settingsTemporary = Path.Combine(directory, $"settings.json.{Guid.NewGuid():N}.tmp");
        var settingsBackup = Path.Combine(directory, $"settings.json.backup.{Guid.NewGuid():N}.tmp");
        var credentialsBackup = Path.Combine(directory, $"credentials.dat.backup.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(settingsTemporary, "{\"baseUrl\":\"https://new.example.test\"}");
        File.WriteAllText(settingsBackup, "{\"baseUrl\":\"https://old.example.test\"}");
        File.WriteAllText(
            Path.Combine(directory, "persistence.transaction.json"),
            JsonSerializer.Serialize(new
            {
                settingsTemporary,
                credentialsTemporary = (string?)null,
                settingsBackup,
                credentialsBackup,
                credentialsChanged = false,
                credentialsExpected = false,
                settingsOriginallyExists = true,
                credentialsOriginallyExists = false,
                credentialsCommitted = false,
                settingsBackedUp = true,
                credentialsBackedUp = false,
                settingsCommitted = false,
                commitCompleted = false
            }));

        var store = new AppSettingsStore(
            directory,
            new UnavailableCredentialProtector("unit test unavailable protector"));
        var recovered = store.Load().Settings;

        Assert(recovered.BaseUrl == "https://old.example.test",
            "An incomplete transaction did not restore the previous settings file.");
        Assert(File.Exists(Path.Combine(directory, "settings.json")),
            "Transaction recovery did not restore settings.json.");
        Assert(!File.Exists(Path.Combine(directory, "persistence.transaction.json")) &&
               !File.Exists(settingsTemporary) &&
               !File.Exists(settingsBackup),
            "Transaction recovery left journal or temporary files behind.");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void TestUnavailableCredentialStoragePreservesUnreadableFiles()
{
    var directory = Path.Combine(Path.GetTempPath(), "AIHubRouter.Tests", Guid.NewGuid().ToString("N"));
    var key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
    try
    {
        using var protector = new AesGcmCredentialProtector(key);
        var writableStore = new AppSettingsStore(directory, protector);
        writableStore.Save(
            new PersistentAppSettings { BaseUrl = "https://stored.example.test" },
            new PersistentCredentials { BearerToken = "stored-token" });
        var oldCredentials = File.ReadAllBytes(Path.Combine(directory, "credentials.dat"));

        var unavailableStore = new AppSettingsStore(
            directory,
            new UnavailableCredentialProtector("unit test unavailable protector"));
        var snapshot = unavailableStore.Load();
        Assert(snapshot.Credentials is null && snapshot.CredentialsUnavailable,
            "An unreadable credential file was not reported as unavailable.");

        unavailableStore.Save(snapshot.Settings with { BaseUrl = "https://settings-only.example.test" }, null);
        Assert(oldCredentials.SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "credentials.dat"))),
            "Saving ordinary settings deleted an unreadable credential file.");
    }
    finally
    {
        Array.Clear(key);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void TestEmptyCredentialsDoNotCreateCredentialFile()
{
    var directory = Path.Combine(Path.GetTempPath(), "AIHubRouter.Tests", Guid.NewGuid().ToString("N"));
    var key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
    try
    {
        using var protector = new AesGcmCredentialProtector(key);
        var store = new AppSettingsStore(directory, protector);
        store.Save(
            new PersistentAppSettings(),
            new PersistentCredentials { BearerToken = "temporary-token" });
        store.Save(new PersistentAppSettings(), new PersistentCredentials());

        Assert(File.Exists(Path.Combine(directory, "settings.json")),
            "Ordinary settings were not saved when no credentials were supplied.");
        Assert(!File.Exists(Path.Combine(directory, "credentials.dat")),
            "An empty credential record did not remove the existing credential file.");
    }
    finally
    {
        Array.Clear(key);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void TestProviderSeriesSettingsRoundtrip()
{
    var directory = Path.Combine(Path.GetTempPath(), "AIHubRouter.Tests", Guid.NewGuid().ToString("N"));
    try
    {
        var store = new AppSettingsStore(
            directory,
            new UnavailableCredentialProtector("unit test unavailable protector"));
        store.Save(
            new PersistentAppSettings
            {
                ProviderSeriesWeight = 0.35,
                ProviderSeriesCacheSeconds = 600,
                ProviderSeriesRange = "12h",
                ProviderSeriesTimezone = "UTC"
            },
            null);

        var loaded = store.Load().Settings;
        var policy = loaded.CreatePolicy();
        Assert(Math.Abs(policy.ProviderSeriesWeight - 0.35) < 0.0001,
            "Provider series weight did not reach the routing policy.");
        Assert(loaded.ProviderSeriesCacheSeconds == 600 &&
               loaded.ProviderSeriesRange == "12h" &&
               loaded.ProviderSeriesTimezone == "UTC",
            "Provider series request and cache settings did not roundtrip.");

        File.WriteAllText(
            Path.Combine(directory, "settings.json"),
            """{"providerSeriesRange":null,"providerSeriesTimezone":null}""");
        var normalized = store.Load().Settings;
        Assert(normalized.ProviderSeriesRange == "6h" &&
               normalized.ProviderSeriesTimezone == "Asia/Shanghai",
            "Null legacy provider series settings were not normalized.");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void TestGroupStickinessPersistsAsPolicyOverride()
{
    var directory = Path.Combine(Path.GetTempPath(), "AIHubRouter.Tests", Guid.NewGuid().ToString("N"));
    try
    {
        var store = new AppSettingsStore(
            directory,
            new UnavailableCredentialProtector("unit test unavailable protector"));
        store.Save(new PersistentAppSettings { GroupStickiness = 0.42 }, null);

        var policy = store.Load().Settings.CreatePolicy();
        Assert(Math.Abs(policy.MinimumScoreAdvantageToSwitch - 0.42) < 0.0001,
            "Persisted group stickiness did not reach the routing policy.");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void TestAuditLogWritesValidJsonAndRotates()
{
    var directory = Path.Combine(Path.GetTempPath(), "AIHubRouter.Tests", Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        var path = Path.Combine(directory, "router.jsonl");
        var writer = new AuditLogWriter(path, maximumMegabytes: 1, retainedFiles: 2);
        var payload = new string('x', 600_000);
        writer.Write(new { schemaVersion = 2, eventType = "test", payload });
        writer.Write(new { schemaVersion = 2, eventType = "test", payload });

        Assert(File.Exists(path), "Audit log was not created.");
        Assert(File.Exists(path + ".1"), "Audit log did not rotate at the configured size.");
        using (JsonDocument.Parse(File.ReadAllText(path)))
        {
        }
        using (JsonDocument.Parse(File.ReadAllText(path + ".1")))
        {
        }

        if (!OperatingSystem.IsWindows())
        {
            var directoryMode = File.GetUnixFileMode(directory);
            Assert(directoryMode.HasFlag(UnixFileMode.GroupRead),
                "Audit writer changed permissions on an existing directory.");
            Assert(File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                "Audit log permissions are not 0600.");
        }
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static RouteEvaluation EvaluationForSwitch(DateTimeOffset now, double currentRate, double targetRate)
{
    return RoutingEngine.Evaluate(
        new[]
        {
            Provider(1, currentRate, true, 0.99, now, latency: 10_000),
            Provider(2, targetRate, true, 0.99, now, latency: 1_000)
        },
        new[] { Group(1), Group(2) },
        new Dictionary<long, double>(),
        Policy(RoutingMode.Balanced),
        now);
}

static BalancedRoutingPolicy Policy(RoutingMode mode) => new()
{
    Mode = mode
};

static void TestEncryptedSettingsRoundtrip()
{
    if (!OperatingSystem.IsWindows())
    {
        return;
    }

    var directory = Path.Combine(Path.GetTempPath(), "AIHubRouter.Tests", Guid.NewGuid().ToString("N"));
    const string secretToken = "unit-test-secret-token";
    try
    {
        var store = new AppSettingsStore(directory);
        var settings = new PersistentAppSettings
        {
            PersistCredentials = true,
            BaseUrl = "https://example.test",
            Platform = "openai",
            PollingIntervalSeconds = 120,
            SmoothRendering = true,
            KeySelectionInitialized = true,
            SelectedKeyIds = [42, 84],
            LunaSelectedKeyIds = [126]
        };
        var expiresAt = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var credentials = new PersistentCredentials
        {
            Email = "distribution-test@example.test",
            Password = "unit-test-password",
            BearerToken = secretToken,
            RefreshToken = "unit-test-refresh-token",
            AccessTokenExpiresAt = expiresAt,
            Cookie = "session=secret-cookie",
            UserAgent = "test-user-agent"
        };

        store.Save(settings, credentials);
        var encrypted = File.ReadAllBytes(Path.Combine(directory, "credentials.dat"));
        var encryptedText = Encoding.UTF8.GetString(encrypted);
        Assert(!encryptedText.Contains(secretToken, StringComparison.Ordinal), "Credential file contains plaintext access token.");
        Assert(!encryptedText.Contains(credentials.RefreshToken, StringComparison.Ordinal), "Credential file contains plaintext refresh token.");
        Assert(!encryptedText.Contains(credentials.Password, StringComparison.Ordinal), "Credential file contains plaintext password.");
        Assert(!encryptedText.Contains(credentials.Email, StringComparison.Ordinal), "Credential file contains plaintext email.");
        Assert(!encryptedText.Contains(credentials.Cookie, StringComparison.Ordinal), "Credential file contains plaintext Cookie.");
        Assert(!encryptedText.Contains(credentials.UserAgent, StringComparison.Ordinal), "Credential file contains plaintext User-Agent.");
        var settingsText = File.ReadAllText(Path.Combine(directory, "settings.json"));
        Assert(!settingsText.Contains(credentials.Email, StringComparison.Ordinal), "Plain settings contain the login email.");
        Assert(!settingsText.Contains(credentials.Password, StringComparison.Ordinal), "Plain settings contain the password.");
        Assert(!settingsText.Contains(secretToken, StringComparison.Ordinal), "Plain settings contain the access token.");
        Assert(!settingsText.Contains(credentials.RefreshToken, StringComparison.Ordinal), "Plain settings contain the refresh token.");

        var loaded = store.Load();
        Assert(loaded.Settings.PersistCredentials, "Persistence flag was not restored.");
        Assert(loaded.Settings.PollingIntervalSeconds == 120, "Polling interval was not restored.");
        Assert(loaded.Settings.KeySelectionInitialized, "Key selection initialized state was not restored.");
        Assert(loaded.Settings.SelectedKeyIds.SequenceEqual(new long[] { 42, 84 }), "Selected Key IDs were not restored.");
        Assert(loaded.Settings.LunaSelectedKeyIds.SequenceEqual(new long[] { 126 }), "Luna Selected Key IDs were not restored.");
        Assert(loaded.Credentials?.Email == credentials.Email, "Encrypted email did not roundtrip.");
        Assert(loaded.Credentials?.Password == credentials.Password, "Encrypted password did not roundtrip.");
        Assert(loaded.Credentials?.BearerToken == secretToken, "Encrypted token did not roundtrip.");
        Assert(loaded.Credentials?.RefreshToken == credentials.RefreshToken, "Encrypted refresh token did not roundtrip.");
        Assert(loaded.Credentials?.AccessTokenExpiresAt == expiresAt, "Access token expiration did not roundtrip.");
        Assert(loaded.Credentials?.Cookie == credentials.Cookie, "Encrypted cookie did not roundtrip.");

        store.Save(new PersistentAppSettings { PersistCredentials = false }, null);
        Assert(!File.Exists(Path.Combine(directory, "credentials.dat")), "Credential file was not removed after disabling persistence.");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void TestUsableAccessTokenIsReused()
{
    var now = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero);
    var refreshCalls = 0;
    var loginCalls = 0;
    var persistCalls = 0;
    var existing = new AuthSession("access-current", "refresh-current", now.AddMinutes(10));
    var coordinator = new SessionCoordinator(
        (refreshToken, cancellationToken) =>
        {
            refreshCalls++;
            return Task.FromResult(new AuthSession("access-refreshed", "refresh-refreshed", now.AddHours(1)));
        },
        (credentials, cancellationToken) =>
        {
            loginCalls++;
            return Task.FromResult(new AuthSession("access-login", "refresh-login", now.AddHours(1)));
        },
        (session, cancellationToken) =>
        {
            persistCalls++;
            return Task.CompletedTask;
        },
        () => now);

    var result = coordinator.GetSessionAsync(
        existing,
        new LoginCredentials("user@example.test", "password"),
        CancellationToken.None).GetAwaiter().GetResult();

    Assert(ReferenceEquals(result, existing), "Coordinator did not reuse the current session instance.");
    Assert(refreshCalls == 0, "Refresh was called for a usable access token.");
    Assert(loginCalls == 0, "Login was called for a usable access token.");
    Assert(persistCalls == 0, "Unchanged session was persisted unnecessarily.");
}

static void TestExpiredAccessTokenRefreshesFirst()
{
    var now = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero);
    var refreshCalls = 0;
    var loginCalls = 0;
    AuthSession? persisted = null;
    var refreshed = new AuthSession("access-refreshed", "refresh-rotated", now.AddHours(1));
    var coordinator = new SessionCoordinator(
        (refreshToken, cancellationToken) =>
        {
            refreshCalls++;
            Assert(refreshToken == "refresh-current", "Coordinator passed the wrong refresh token.");
            return Task.FromResult(refreshed);
        },
        (credentials, cancellationToken) =>
        {
            loginCalls++;
            return Task.FromResult(new AuthSession("access-login", "refresh-login", now.AddHours(1)));
        },
        (session, cancellationToken) =>
        {
            persisted = session;
            return Task.CompletedTask;
        },
        () => now);

    var result = coordinator.GetSessionAsync(
        new AuthSession("access-expired", "refresh-current", now.AddSeconds(-1)),
        new LoginCredentials("user@example.test", "password"),
        CancellationToken.None).GetAwaiter().GetResult();

    Assert(ReferenceEquals(result, refreshed), "Coordinator did not return the refreshed session.");
    Assert(refreshCalls == 1, "Refresh was not called exactly once.");
    Assert(loginCalls == 0, "Login was called after a successful refresh.");
    Assert(ReferenceEquals(persisted, refreshed), "Rotated refresh token was not persisted.");
}

static void TestRejectedRefreshFallsBackToLogin()
{
    var now = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero);
    var refreshCalls = 0;
    var loginCalls = 0;
    var persistCalls = 0;
    var loggedIn = new AuthSession("access-login", "refresh-login", now.AddHours(1));
    var coordinator = new SessionCoordinator(
        (refreshToken, cancellationToken) =>
        {
            refreshCalls++;
            throw new AIHubApiException("Refresh rejected.", HttpStatusCode.Unauthorized, "INVALID_TOKEN");
        },
        (credentials, cancellationToken) =>
        {
            loginCalls++;
            Assert(credentials.Email == "user@example.test", "Coordinator passed the wrong email.");
            Assert(credentials.Password == "password", "Coordinator passed the wrong password.");
            return Task.FromResult(loggedIn);
        },
        (session, cancellationToken) =>
        {
            persistCalls++;
            Assert(ReferenceEquals(session, loggedIn), "Coordinator persisted the rejected session.");
            return Task.CompletedTask;
        },
        () => now);

    var result = coordinator.GetSessionAsync(
        new AuthSession("access-expired", "refresh-rejected", now.AddMinutes(-5)),
        new LoginCredentials("user@example.test", "password"),
        CancellationToken.None).GetAwaiter().GetResult();

    Assert(ReferenceEquals(result, loggedIn), "Coordinator did not return the login session.");
    Assert(refreshCalls == 1, "Rejected refresh was not attempted exactly once.");
    Assert(loginCalls == 1, "Login fallback was not attempted exactly once.");
    Assert(persistCalls == 1, "Login session was not persisted exactly once.");
}

static void TestRefreshApiCodeFallsBackToLogin()
{
    var handler = new StubHttpMessageHandler(request => JsonResponse("""
        {"code":"invalid_grant","message":"refresh rejected","data":null}
        """));
    using var client = new AIHubClient("https://example.test", messageHandler: handler);
    var loginCalls = 0;
    var coordinator = new SessionCoordinator(
        client.RefreshSessionAsync,
        (credentials, cancellationToken) =>
        {
            loginCalls++;
            return Task.FromResult(new AuthSession("access-login", "refresh-login", DateTimeOffset.UtcNow.AddHours(1)));
        },
        (session, cancellationToken) => Task.CompletedTask);

    var session = coordinator.GetSessionAsync(
        new AuthSession("access-expired", "refresh-rejected", DateTimeOffset.MinValue),
        new LoginCredentials("user@example.test", "password"),
        CancellationToken.None).GetAwaiter().GetResult();

    Assert(loginCalls == 1, "HTTP 200 invalid_grant did not trigger login fallback.");
    Assert(session.AccessToken == "access-login", "Login fallback session was not returned.");
}

static void TestAuthenticationApiCodeIsClassified()
{
    var exception = new AIHubApiException("Synthetic auth failure.", HttpStatusCode.OK, "401");
    Assert(exception.IsAuthenticationFailure, "API code 401 was not classified as an authentication failure.");
}

static void TestRefreshNetworkFailureDoesNotLogIn()
{
    var loginCalls = 0;
    var coordinator = new SessionCoordinator(
        (refreshToken, cancellationToken) => throw new HttpRequestException("Synthetic network failure."),
        (credentials, cancellationToken) =>
        {
            loginCalls++;
            return Task.FromResult(new AuthSession("access-login", "refresh-login", DateTimeOffset.UtcNow.AddHours(1)));
        },
        (session, cancellationToken) => Task.CompletedTask);

    try
    {
        coordinator.GetSessionAsync(
            new AuthSession("access-expired", "refresh-current", DateTimeOffset.MinValue),
            new LoginCredentials("user@example.test", "password"),
            CancellationToken.None).GetAwaiter().GetResult();
        throw new InvalidOperationException("Network failure was swallowed.");
    }
    catch (HttpRequestException)
    {
        Assert(loginCalls == 0, "Network failure incorrectly triggered password login.");
    }
}

static void TestLoginEndpointMapsSession()
{
    var now = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
    var handler = new StubHttpMessageHandler(request =>
    {
        Assert(request.Method == HttpMethod.Post, "Login did not use POST.");
        Assert(request.RequestUri?.AbsolutePath == "/api/v1/auth/login", "Login used the wrong endpoint.");
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        Assert(body.Contains("user@example.test", StringComparison.Ordinal), "Login request omitted the email.");
        Assert(body.Contains("synthetic-password", StringComparison.Ordinal), "Login request omitted the password.");
        return JsonResponse("""
            {"code":0,"message":"ok","data":{"access_token":"access-login","refresh_token":"refresh-login","expires_in":3600,"token_type":"Bearer","user":{"email":"user@example.test"}}}
            """);
    });
    using var client = new AIHubClient(
        "https://example.test",
        messageHandler: handler,
        utcNow: () => now);

    var session = client.LoginAsync(
        new LoginCredentials("user@example.test", "synthetic-password"),
        CancellationToken.None).GetAwaiter().GetResult();

    Assert(session.AccessToken == "access-login", "Login access token was not mapped.");
    Assert(session.RefreshToken == "refresh-login", "Login refresh token was not mapped.");
    Assert(session.ExpiresAt == now.AddSeconds(3600), "Login expiration was not converted to an absolute time.");
}

static void TestRefreshEndpointMapsRotatedSession()
{
    var now = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero);
    var handler = new StubHttpMessageHandler(request =>
    {
        Assert(request.RequestUri?.AbsolutePath == "/api/v1/auth/refresh", "Refresh used the wrong endpoint.");
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        Assert(body.Contains("refresh-old", StringComparison.Ordinal), "Refresh request omitted the refresh token.");
        return JsonResponse("""
            {"code":0,"message":"ok","data":{"access_token":"access-new","refresh_token":"refresh-new","expires_in":1800,"token_type":"Bearer"}}
            """);
    });
    using var client = new AIHubClient(
        "https://example.test",
        messageHandler: handler,
        utcNow: () => now);

    var session = client.RefreshSessionAsync("refresh-old", CancellationToken.None).GetAwaiter().GetResult();

    Assert(session.AccessToken == "access-new", "Refreshed access token was not mapped.");
    Assert(session.RefreshToken == "refresh-new", "Rotated refresh token was not mapped.");
    Assert(session.ExpiresAt == now.AddSeconds(1800), "Refresh expiration was not converted to an absolute time.");
}

static void TestRefreshKeepsTokenWhenServerOmitsRotation()
{
    var handler = new StubHttpMessageHandler(request => JsonResponse("""
        {"code":0,"message":"ok","data":{"access_token":"access-new","expires_in":1800,"token_type":"Bearer"}}
        """));
    using var client = new AIHubClient("https://example.test", messageHandler: handler);

    var session = client.RefreshSessionAsync("refresh-current", CancellationToken.None).GetAwaiter().GetResult();

    Assert(session.RefreshToken == "refresh-current", "Refresh discarded the existing token when no rotation was returned.");
}

static void TestAuthenticationErrorHidesServerMessage()
{
    const string sensitiveMessage = "synthetic-email@example.test synthetic-temporary-token";
    var handler = new StubHttpMessageHandler(request => JsonResponse(
        "{\"code\":\"invalid_grant\",\"message\":\"" + sensitiveMessage + "\",\"data\":null}"));
    using var client = new AIHubClient("https://example.test", messageHandler: handler);

    try
    {
        client.RefreshSessionAsync("refresh-current", CancellationToken.None).GetAwaiter().GetResult();
        throw new InvalidOperationException("Rejected refresh was accepted.");
    }
    catch (AIHubApiException exception)
    {
        Assert(exception.ApiCode == "invalid_grant", "Authentication error discarded the safe API code.");
        Assert(!exception.Message.Contains(sensitiveMessage, StringComparison.Ordinal), "Authentication error exposed the server message.");
    }
}

static void TestBusinessErrorHidesServerMessage()
{
    const string sensitiveMessage = "synthetic-cookie=session-value synthetic-key=sk-secret";
    var handler = new StubHttpMessageHandler(request => JsonResponse(
        "{\"code\":\"500\",\"message\":\"" + sensitiveMessage + "\",\"data\":null}"));
    using var client = new AIHubClient("https://example.test", messageHandler: handler);

    try
    {
        client.GetAvailableGroupsAsync(CancellationToken.None).GetAwaiter().GetResult();
        throw new InvalidOperationException("Rejected business response was accepted.");
    }
    catch (AIHubApiException exception)
    {
        Assert(exception.ApiCode == "500", "Business error discarded the API code.");
        Assert(!exception.Message.Contains(sensitiveMessage, StringComparison.Ordinal), "Business error exposed the server message.");
    }
}

static void TestAuthenticationRejectionShowsStatusHint()
{
    const string serverMessage = "invalid email or password";
    var handler = new StubHttpMessageHandler(request =>
    {
        Assert(request.RequestUri?.AbsolutePath == "/api/v1/auth/login", "Login used the wrong endpoint.");
        return new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                "{\"code\":401,\"message\":\"" + serverMessage + "\",\"reason\":\"INVALID_CREDENTIALS\"}",
                Encoding.UTF8,
                "application/json")
        };
    });
    using var client = new AIHubClient("https://example.test", messageHandler: handler);

    try
    {
        client.LoginAsync(
            new LoginCredentials("user@example.test", "wrong-password"),
            CancellationToken.None).GetAwaiter().GetResult();
        throw new InvalidOperationException("Rejected login was accepted.");
    }
    catch (AIHubApiException exception)
    {
        Assert(exception.ApiCode == "401", "Authentication rejection discarded the API code.");
        Assert(
            exception.Message.Contains("邮箱或密码错误", StringComparison.Ordinal),
            "Authentication rejection lacks a status hint.");
        Assert(
            !exception.Message.Contains(serverMessage, StringComparison.Ordinal),
            "Authentication rejection exposed the server message.");
    }
}

static void TestAuthenticationValidationErrorShowsFormatHint()
{
    var handler = new StubHttpMessageHandler(request =>
    {
        Assert(request.RequestUri?.AbsolutePath == "/api/v1/auth/login", "Login used the wrong endpoint.");
        return new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"code\":400,\"message\":\"Invalid request: Password is required\"}",
                Encoding.UTF8,
                "application/json")
        };
    });
    using var client = new AIHubClient("https://example.test", messageHandler: handler);

    try
    {
        client.LoginAsync(
            new LoginCredentials("user@example.test", "password"),
            CancellationToken.None).GetAwaiter().GetResult();
        throw new InvalidOperationException("Rejected login was accepted.");
    }
    catch (AIHubApiException exception)
    {
        Assert(exception.ApiCode == "400", "Validation rejection discarded the API code.");
        Assert(
            exception.Message.Contains("认证请求无效", StringComparison.Ordinal),
            "Validation rejection lacks a format hint.");
    }
}

static void TestInteractiveLoginRequirementIsRejected()
{
    const string temporaryToken = "temporary-two-factor-token-must-not-leak";
    var responseJson = "{\"code\":0,\"message\":\"ok\",\"data\":{\"requires_2fa\":true,\"temp_token\":\"" +
        temporaryToken +
        "\",\"user_email_masked\":\"u***@example.test\"}}";
    var handler = new StubHttpMessageHandler(request => JsonResponse(responseJson));
    using var client = new AIHubClient("https://example.test", messageHandler: handler);

    try
    {
        client.LoginAsync(
            new LoginCredentials("user@example.test", "synthetic-password"),
            CancellationToken.None).GetAwaiter().GetResult();
        throw new InvalidOperationException("Interactive authentication response was accepted.");
    }
    catch (InteractiveAuthenticationRequiredException exception)
    {
        Assert(!exception.Message.Contains(temporaryToken, StringComparison.Ordinal), "Interactive auth error leaked the temporary token.");
    }
}

static void TestCloudflareJsChallengeIsDetected()
{
    var handler = new StubHttpMessageHandler(_ => ChallengeResponse("""
        <!DOCTYPE html>
        <html>
          <head><title>Just a moment...</title></head>
          <body>
            <div class="main-wrapper" role="main">
              <div class="ctp-checkbox-label">Enable JavaScript and cookies to continue</div>
            </div>
          </body>
        </html>
        """));
    using var client = new AIHubClient("https://example.test", messageHandler: handler);

    try
    {
        client.GetAvailableGroupsAsync(CancellationToken.None).GetAwaiter().GetResult();
        throw new InvalidOperationException("Cloudflare challenge was accepted.");
    }
    catch (CloudflareChallengeException exception)
    {
        Assert(exception.ChallengeKind == CloudflareChallengeKind.JsChallenge, "JS challenge was misclassified.");
        Assert(!exception.IsAuthenticationFailure, "Cloudflare challenge was classified as an authentication failure.");
    }
}

static void TestCloudflareInteractiveChallengeIsDetected()
{
    var handler = new StubHttpMessageHandler(_ => ChallengeResponse("""
        <!DOCTYPE html>
        <html>
          <head><title>Attention Required! | Cloudflare</title></head>
          <body>
            <div class="cf-chl-widget"><label>Verify you are human</label></div>
          </body>
        </html>
        """));
    using var client = new AIHubClient("https://example.test", messageHandler: handler);

    try
    {
        client.GetAvailableGroupsAsync(CancellationToken.None).GetAwaiter().GetResult();
        throw new InvalidOperationException("Interactive challenge was accepted.");
    }
    catch (CloudflareChallengeException exception)
    {
        Assert(exception.ChallengeKind == CloudflareChallengeKind.InteractiveChallenge, "Interactive challenge was misclassified.");
    }
}

static void TestCloudflareChallengeSolverRetriesWithCookies()
{
    var callCount = 0;
    string? seenCookieHeader = null;
    string? seenUserAgent = null;
    var handler = new StubHttpMessageHandler(request =>
    {
        callCount++;
        seenCookieHeader = request.Headers.TryGetValues("Cookie", out var values)
            ? string.Join("; ", values)
            : null;
        seenUserAgent = request.Headers.TryGetValues("User-Agent", out var agents)
            ? string.Join("; ", agents)
            : null;
        if (callCount == 1)
        {
            return ChallengeResponse("""
                <!DOCTYPE html>
                <html>
                  <head><title>Just a moment...</title></head>
                  <body></body>
                </html>
                """);
        }

        return JsonResponse("""
            {"code":0,"message":"ok","data":[{"id":1,"name":"Group 1","platform":"openai","status":"active"}]}
            """);
    });
    using var solver = new StubCloudflareChallengeSolver(_ =>
        new CloudflareChallengeSolution(
            "SyntheticBrowser/1.0",
            new Dictionary<string, string>
            {
                ["cf_clearance"] = "clearance-token",
                ["__cf_bm"] = "bm-token"
            }));
    using var client = new AIHubClient(
        "https://example.test",
        messageHandler: handler,
        cloudflareChallengeSolver: solver);

    var groups = client.GetAvailableGroupsAsync(CancellationToken.None).GetAwaiter().GetResult();

    Assert(callCount == 2, "Solver retry did not send exactly two requests.");
    Assert(solver.SolveCalls == 1, "Solver was not invoked exactly once.");
    Assert(
        seenCookieHeader is not null &&
        seenCookieHeader.Contains("cf_clearance=clearance-token", StringComparison.Ordinal),
        "Solved cookie was not attached to the retry.");
    Assert(
        seenUserAgent == "SyntheticBrowser/1.0",
        "Solver-provided User-Agent was not used for the retry.");
    Assert(groups.Count == 1, "Successful retry did not parse the response.");
}

static void TestCloudflareSolverFailureDoesNotRetryForever()
{
    var callCount = 0;
    var handler = new StubHttpMessageHandler(_ =>
    {
        callCount++;
        return ChallengeResponse("""
            <!DOCTYPE html>
            <html>
              <head><title>Just a moment...</title></head>
              <body></body>
            </html>
            """);
    });
    using var solver = new StubCloudflareChallengeSolver(_ => null);
    using var client = new AIHubClient(
        "https://example.test",
        messageHandler: handler,
        cloudflareChallengeSolver: solver);

    try
    {
        client.GetAvailableGroupsAsync(CancellationToken.None).GetAwaiter().GetResult();
        throw new InvalidOperationException("Unsolved challenge was accepted.");
    }
    catch (CloudflareChallengeException)
    {
        Assert(callCount == 1, "Solver failure triggered more than one request.");
        Assert(solver.SolveCalls == 1, "Solver was invoked more than once.");
    }
}

static void TestJsonBusinessErrorIsNotCloudflareChallenge()
{
    var handler = new StubHttpMessageHandler(_ =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"code":403,"message":"forbidden","data":null}""", Encoding.UTF8, "application/json")
        };
        response.Headers.TryAddWithoutValidation("Server", "cloudflare");
        return response;
    });
    using var client = new AIHubClient("https://example.test", messageHandler: handler);

    try
    {
        client.GetAvailableGroupsAsync(CancellationToken.None).GetAwaiter().GetResult();
        throw new InvalidOperationException("JSON 403 was accepted.");
    }
    catch (CloudflareChallengeException)
    {
        throw new InvalidOperationException("JSON 403 was misclassified as a Cloudflare challenge.");
    }
    catch (AIHubApiException exception)
    {
        Assert(exception.StatusCode == HttpStatusCode.Forbidden, "JSON 403 lost its status code.");
    }
}
static void TestEmptyKeySelectionRoundtrips()
{
    if (!OperatingSystem.IsWindows())
    {
        return;
    }

    var directory = Path.Combine(Path.GetTempPath(), "AIHubRouter.Tests", Guid.NewGuid().ToString("N"));
    try
    {
        var store = new AppSettingsStore(directory);
        store.Save(new PersistentAppSettings
        {
            PersistCredentials = false,
            KeySelectionInitialized = true,
            SelectedKeyIds = []
        }, null);

        var loaded = store.Load();
        Assert(loaded.Settings.KeySelectionInitialized, "Explicit empty selection lost its initialized state.");
        Assert(loaded.Settings.SelectedKeyIds.Length == 0, "Explicit empty selection was not preserved.");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void TestFirstKeySelectionChoosesFirstActiveKey()
{
    var selected = KeySelectionPolicy.Resolve(
        initialized: false,
        savedIds: [],
        keys:
        [
            new ApiKeyInfo { Id = 10, Status = "disabled" },
            new ApiKeyInfo { Id = 20, Status = "active" },
            new ApiKeyInfo { Id = 30, Status = "active" }
        ]);

    Assert(selected.SequenceEqual(new long[] { 20 }), "First load did not select only the first active Key.");
}

static void TestInitializedEmptyKeySelectionStaysEmpty()
{
    var keys = new[]
    {
        new ApiKeyInfo { Id = 10, Status = "active" },
        new ApiKeyInfo { Id = 20, Status = "active" }
    };
    var empty = KeySelectionPolicy.Resolve(initialized: true, savedIds: [], keys);
    var restored = KeySelectionPolicy.Resolve(initialized: true, savedIds: [20, 999], keys);

    Assert(empty.Count == 0, "An initialized empty selection selected a Key again.");
    Assert(restored.SequenceEqual(new long[] { 20 }), "Saved selection did not ignore unavailable Key IDs.");
}

static HttpResponseMessage JsonResponse(string json)
{
    return new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}

static HttpResponseMessage ChallengeResponse(string body, HttpStatusCode status = HttpStatusCode.Forbidden)
{
    var response = new HttpResponseMessage(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/html")
    };
    response.Headers.TryAddWithoutValidation("Server", "cloudflare");
    response.Headers.TryAddWithoutValidation("CF-Ray", "test-ray-hkg");
    return response;
}

static ProviderStatus Provider(
    long groupId,
    double rate,
    bool available,
    double success,
    DateTimeOffset checkedAt,
    double? latency = 1000,
    bool warning = false,
    double confidence = 1,
    double? cacheHitRate = null)
{
    return new ProviderStatus
    {
        Id = $"provider-{groupId}",
        GroupId = groupId,
        PlanType = $"Plan {groupId}",
        Platform = "openai",
        PriceMultiplier = rate,
        Available = available,
        Enabled = true,
        CheckedAt = checkedAt,
        FirstTokenLatencyMs = latency,
        CacheHitRate = cacheHitRate,
        LatencyConfidence = confidence,
        SuccessRates = new Dictionary<string, double?> { ["6h"] = success },
        WarningReasons = warning
            ? [new ProviderWarningReason { Type = "test_warning", Message = "Synthetic warning" }]
            : []
    };
}

static GroupInfo Group(long id)
{
    return new GroupInfo
    {
        Id = id,
        Name = $"Group {id}",
        Platform = "openai",
        RateMultiplier = 1,
        Status = "active"
    };
}

static RoutingCriteria Criteria() => new("openai", TimeSpan.FromMinutes(15));

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(responder(request));
    }
}

sealed class StubCloudflareChallengeSolver(
    Func<Uri, CloudflareChallengeSolution?> solver) : ICloudflareChallengeSolver
{
    public int SolveCalls { get; private set; }

    public Task<CloudflareChallengeSolution?> SolveAsync(
        Uri origin,
        CancellationToken cancellationToken)
    {
        SolveCalls++;
        return Task.FromResult(solver(origin));
    }

    public void Dispose()
    {
    }
}

sealed class MemoryRouteStateStore(RouteState? initial = null) : IRouteStateStore
{
    private RouteState _state = initial ?? new();
    public RouteState Current => _state;
    public RouteState Load() => _state;
    public void Save(RouteState state) => _state = state;
}

sealed class ThrowingCredentialProtector : ICredentialProtector
{
    public bool IsAvailable => true;
    public string Description => "unit test throwing protector";

    public byte[] Protect(ReadOnlySpan<byte> plaintext) =>
        throw new InvalidOperationException("unit test protection failure");

    public byte[] Unprotect(ReadOnlySpan<byte> encrypted) =>
        throw new InvalidOperationException("unit test unprotect failure");
}

sealed class StubAIHubClientFactory(IAIHubApiClient client) : IAIHubClientFactory
{
    public IAIHubApiClient Create(
        string baseUrl,
        string? bearerToken,
        string? cookie,
        string? userAgent,
        bool allowInsecureLoopback,
        ICloudflareChallengeSolver? cloudflareChallengeSolver = null) => client;
}

sealed class StubAIHubApiClient(
    DateTimeOffset now,
    IReadOnlyList<ApiKeyInfo>? keys = null,
    Func<int, long, long, Exception?>? updateFailure = null,
    bool supportsRefresh = false,
    Func<int, ProviderSeriesPage>? providerSeries = null,
    Func<int, ProviderCacheHitRatePage>? providerCacheHitRates = null) : IAIHubApiClient
{
    public int UpdateCalls { get; private set; }
    public int ProviderSeriesCalls { get; private set; }
    public int ProviderCacheHitRateCalls { get; private set; }

    public Task<GroupUsageStatsPage> GetGroupUsageStatsAsync(
        string platform,
        int samples = 100,
        CancellationToken cancellationToken = default,
        double? maxRate = null) =>
        Task.FromResult(new GroupUsageStatsPage
        {
            SampleLimit = samples,
            Items =
            [
                new GroupUsageStat
                {
                    Code = "Fast",
                    GroupId = 2,
                    Platform = "openai",
                    RateMultiplier = 0.02,
                    AverageTtftMs = 500,
                    SampleCount = samples,
                    LastSampleAt = now
                }
            ]
        });

    public Task<ProviderSeriesPage> GetProviderSeriesAsync(
        string range,
        string timezone,
        CancellationToken cancellationToken = default)
    {
        ProviderSeriesCalls++;
        var page = providerSeries?.Invoke(ProviderSeriesCalls) ??
            new ProviderSeriesPage(
                now,
                range,
                new Dictionary<long, ProviderSeriesMetrics>
                {
                    [2] = new(2, 1, 500, 500, 20, 20, now)
                });
        return Task.FromResult(page);
    }

    public Task<ProviderCacheHitRatePage> GetProviderCacheHitRatesAsync(
        string timezone,
        CancellationToken cancellationToken = default)
    {
        ProviderCacheHitRateCalls++;
        var page = providerCacheHitRates?.Invoke(ProviderCacheHitRateCalls) ??
            new ProviderCacheHitRatePage(
                now,
                new Dictionary<long, double> { [2] = 0.80 });
        return Task.FromResult(page);
    }

    public Task<System.Text.Json.JsonElement> ValidateLoginAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<AuthSession> LoginAsync(LoginCredentials credentials, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<AuthSession> RefreshSessionAsync(string refreshToken, CancellationToken cancellationToken = default) =>
        supportsRefresh
            ? Task.FromResult(new AuthSession("refreshed-token", refreshToken, now.AddHours(1)))
            : throw new NotSupportedException();

    public Task<IReadOnlyList<GroupInfo>> GetAvailableGroupsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GroupInfo>>([GroupForStub(2)]);

    public Task<IReadOnlyDictionary<long, double>> GetUserGroupRatesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<long, double>>(new Dictionary<long, double>());

    public Task<IReadOnlyList<ApiKeyInfo>> GetAllKeysAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ApiKeyInfo>>
        (keys ??
        [
            new ApiKeyInfo { Id = 10, Name = "Key", Status = "active", GroupId = 1 }
        ]);

    public Task<ApiKeyInfo> UpdateKeyGroupAsync(long keyId, long groupId, CancellationToken cancellationToken = default)
    {
        UpdateCalls++;
        if (updateFailure?.Invoke(UpdateCalls, keyId, groupId) is { } failure)
        {
            throw failure;
        }

        return Task.FromResult(new ApiKeyInfo
        {
            Id = keyId,
            Name = "Key",
            Status = "active",
            GroupId = groupId,
            Group = GroupForStub(groupId)
        });
    }

    public void Dispose()
    {
    }

    private static GroupInfo GroupForStub(long id) => new()
    {
        Id = id,
        Name = $"Group {id}",
        Platform = "openai",
        Status = "active"
    };
}
