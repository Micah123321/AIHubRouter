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
    ("Lowest available authorized group", TestLowestAvailableGroup),
    ("Blacklisted group is excluded", TestBlacklistedGroupIsExcluded),
    ("Blacklisted group is excluded from balanced evaluation", TestBalancedEvaluationExcludesBlacklistedGroup),
    ("User rate override", TestUserRateOverride),
    ("Latest status controls eligibility", TestLatestStatusControlsEligibility),
    ("Stale status rejection", TestStaleStatusRejection),
    ("Balanced mode buys meaningful latency", TestBalancedModeBuysLatency),
    ("Balanced mode rejects catastrophic cheap latency", TestBalancedModeRejectsCatastrophicCheapLatency),
    ("Balanced mode keeps price for moderate speed gap", TestBalancedModeKeepsPriceForModerateGap),
    ("Balanced mode keeps cheap route for a common latency gap", TestBalancedModeKeepsCheapRouteInCommonRange),
    ("Balanced mode escapes extreme latency at double price", TestBalancedModeEscapesExtremeLatency),
    ("Balanced mode rejects weak latency value", TestBalancedModeRejectsWeakLatencyValue),
    ("Economy mode protects price", TestEconomyModeProtectsPrice),
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
    ("AES settings roundtrip has no plaintext", TestAesSettingsRoundtrip),
    ("Unavailable credential storage fails before settings write", TestUnavailableCredentialStorageIsAtomic),
    ("Legacy hard-gate settings are ignored", TestLegacyHardGateSettingsAreIgnored),
    ("Audit log writes valid JSON and rotates safely", TestAuditLogWritesValidJsonAndRotates),
    ("Dry run never updates a key", TestDryRunNeverUpdatesKey),
    ("Manual route updates selected keys and state", TestManualRouteUpdatesSelectedKeysAndState),
    ("Manual route rejects blacklisted group", TestManualRouteRejectsBlacklistedGroup),
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
    ("Empty key selection roundtrips", TestEmptyKeySelectionRoundtrips),
    ("First key selection chooses first active key", TestFirstKeySelectionChoosesFirstActiveKey),
    ("Initialized empty key selection stays empty", TestInitializedEmptyKeySelectionStaysEmpty)
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
        var summary = await client.GetProviderSummaryAsync();
        Assert(summary.Apis.Count > 0, "Public provider endpoint returned no entries.");
        Console.WriteLine($"PASS Public API smoke test ({summary.Apis.Count} entries)");
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
          "apis": [
            {
              "id": "provider-null-availability",
              "group_id": 51,
              "planType": "A016-Free",
              "platform": "openai",
              "priceMultiplier": 0.005,
              "available": null,
              "enabled": true
            }
          ]
        }
        """));
    using var client = new AIHubClient("https://example.test", messageHandler: handler);

    var summary = client.GetProviderSummaryAsync(CancellationToken.None).GetAwaiter().GetResult();

    Assert(summary.Apis.Count == 1 && !summary.Apis[0].Available,
        "A null provider availability was not treated as unavailable.");
}

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
        Provider(2, 0.022, true, 0.99, now, latency: 1_000)
    };
    var evaluation = RoutingEngine.Evaluate(
        providers,
        new[] { Group(1), Group(2) },
        new Dictionary<long, double>(),
        Policy(RoutingMode.Economy),
        now);

    Assert(evaluation.Recommended?.Group.Id == 1, "Economy mode paid too much for the latency improvement.");
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

static void TestBalancedModeKeepsPriceForModerateGap()
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

    Assert(evaluation.Recommended?.Group.Id == 1,
        "Balanced mode paid a 67% premium for a moderate latency gap.");
}

static void TestBalancedModeKeepsCheapRouteInCommonRange()
{
    var now = DateTimeOffset.UtcNow;
    var policy = Policy(RoutingMode.Balanced);
    var evaluation = RoutingEngine.Evaluate(
        new[]
        {
            Provider(1, 0.03, true, 0.99, now, latency: 9_000),
            Provider(2, 0.05, true, 0.99, now, latency: 2_000)
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
        "Balanced mode left a usable cheap route for a small weighted advantage.");
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
            Provider(1, 0.0201, true, 0.99, now, latency: 981),
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
            BearerToken = "secret-token"
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
        Assert(!encryptedText.Contains(credentials.Password, StringComparison.Ordinal), "AES file contains password plaintext.");
        Assert(!encryptedText.Contains(credentials.BearerToken, StringComparison.Ordinal), "AES file contains token plaintext.");
        var loaded = store.Load();
        Assert(loaded.Credentials?.Password == credentials.Password, "AES password did not roundtrip.");
        Assert(loaded.Credentials?.BearerToken == credentials.BearerToken, "AES token did not roundtrip.");
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
        Assert(Math.Abs(policy.PriceWeight - 0.90) < 0.0001,
            "Legacy settings changed the balanced price weight.");
        Assert(Math.Abs(policy.LatencyWeight - 0.10) < 0.0001,
            "Legacy settings changed the balanced latency weight.");
        Assert(Math.Abs(policy.MinimumScoreAdvantageToSwitch - 0.05) < 0.0001,
            "Legacy settings changed the score hysteresis threshold.");
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
            SelectedKeyIds = [42, 84]
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

static ProviderStatus Provider(
    long groupId,
    double rate,
    bool available,
    double success,
    DateTimeOffset checkedAt,
    double? latency = 1000,
    bool warning = false)
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
        SuccessRates = new Dictionary<string, double> { ["6h"] = success },
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

sealed class MemoryRouteStateStore(RouteState? initial = null) : IRouteStateStore
{
    private RouteState _state = initial ?? new();
    public RouteState Current => _state;
    public RouteState Load() => _state;
    public void Save(RouteState state) => _state = state;
}

sealed class StubAIHubClientFactory(IAIHubApiClient client) : IAIHubClientFactory
{
    public IAIHubApiClient Create(
        string baseUrl,
        string? bearerToken,
        string? cookie,
        string? userAgent,
        bool allowInsecureLoopback) => client;
}

sealed class StubAIHubApiClient(
    DateTimeOffset now,
    IReadOnlyList<ApiKeyInfo>? keys = null,
    Func<int, long, long, Exception?>? updateFailure = null,
    bool supportsRefresh = false) : IAIHubApiClient
{
    public int UpdateCalls { get; private set; }

    public Task<MonitorSummary> GetProviderSummaryAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new MonitorSummary
        {
            Apis =
            [
                new ProviderStatus
                {
                    Id = "provider-2",
                    GroupId = 2,
                    PlanType = "Fast",
                    Platform = "openai",
                    PriceMultiplier = 0.02,
                    Available = true,
                    Enabled = true,
                    CheckedAt = now,
                    FirstTokenLatencyMs = 500,
                    SuccessRates = new Dictionary<string, double> { ["6h"] = 1 }
                }
            ]
        });

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
