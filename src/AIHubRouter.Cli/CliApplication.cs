using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHubRouter.Browser;
using AIHubRouter.Core;

namespace AIHubRouter.Cli;

internal static class CliApplication
{
    private static readonly Lazy<PlaywrightCloudflareChallengeSolver> CloudflareSolver =
        new(() => new PlaywrightCloudflareChallengeSolver());

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task<int> RunAsync(string[] args)
    {
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        using var sigterm = OperatingSystem.IsWindows()
            ? null
            : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                shutdown.Cancel();
            });

        try
        {
            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintHelp();
                return 0;
            }

            var store = new AppSettingsStore();
            return args[0].ToLowerInvariant() switch
            {
                "auth" => await RunAuthAsync(store, args[1..], shutdown.Token),
                "route" => await RunRouteAsync(store, args[1..], watch: false, shutdown.Token),
                "watch" => await RunRouteAsync(store, args[1..], watch: true, shutdown.Token),
                "status" => await RunStatusAsync(store, args[1..], shutdown.Token),
                "config" => RunConfig(store, args[1..]),
                _ => FailUsage($"未知命令：{args[0]}")
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("已停止。" );
            return 0;
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return exception.Message.Contains("认证", StringComparison.Ordinal) ||
                exception.Message.Contains("凭据", StringComparison.Ordinal)
                ? 3
                : 6;
        }
        catch (AIHubApiException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return exception.IsAuthenticationFailure ? 4 : 5;
        }
        catch (HttpRequestException)
        {
            Console.Error.WriteLine("网络连接失败。" );
            return 5;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine("配置目录不可写。请设置可写的 HOME 或 XDG_CONFIG_HOME。" );
            return 6;
        }
        catch (IOException)
        {
            Console.Error.WriteLine("读取或写入本地配置失败。" );
            return 6;
        }
    }

    private static async Task<int> RunAuthAsync(
        AppSettingsStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            return FailUsage("auth 需要 login 或 import-token 子命令。" );
        }

        var forcePersist = HasFlag(args, "--persist");
        var disablePersist = HasFlag(args, "--no-persist");
        if (forcePersist && disablePersist)
        {
            return FailUsage("--persist 与 --no-persist 不能同时使用。" );
        }

        var snapshot = store.Load();
        var settings = ApplyEnvironmentSettings(snapshot.Settings);
        var credentials = ApplyEnvironmentCredentials(snapshot.Credentials ?? new PersistentCredentials());
        var persist = forcePersist || (!disablePersist && snapshot.Settings.PersistCredentials);
        switch (args[0].ToLowerInvariant())
        {
            case "login":
            {
                var explicitEmail = GetOption(args, "--email");
                var email = explicitEmail ?? Environment.GetEnvironmentVariable("AIHUB_EMAIL");
                if (string.IsNullOrWhiteSpace(email) || !HasFlag(args, "--password-stdin"))
                {
                    return FailUsage("login 需要 --email 和 --password-stdin。" );
                }

                EnsureCredentialPersistenceAvailable(store, persist);
                var password = await ReadSecretLineAsync("Password: ", cancellationToken);
                using var client = new AIHubClient(
                    settings.BaseUrl,
                    cookie: credentials.Cookie,
                    userAgent: credentials.UserAgent,
                    allowInsecureLoopback: settings.AllowInsecureLoopback,
                    cloudflareChallengeSolver: CloudflareSolver.Value);
                var session = await client.LoginAsync(
                    new LoginCredentials(email, password),
                    cancellationToken);
                var storedCredentials = snapshot.Credentials ?? new PersistentCredentials();
                var savedCredentials = storedCredentials with
                {
                    Email = explicitEmail is null ? storedCredentials.Email : email.Trim(),
                    Password = password,
                    BearerToken = session.AccessToken,
                    RefreshToken = session.RefreshToken,
                    AccessTokenExpiresAt = session.ExpiresAt
                };
                try
                {
                    if (persist)
                    {
                        store.Save(snapshot.Settings with { PersistCredentials = true }, savedCredentials);
                    }

                    Console.WriteLine(persist
                        ? $"登录成功，session 已安全保存，有效期至 {session.ExpiresAt:O}。"
                        : $"登录成功，session 本次未保存，有效期至 {session.ExpiresAt:O}。" );
                    return 0;
                }
                finally
                {
                    password = string.Empty;
                }
            }
            case "import-token":
            {
                if (!HasFlag(args, "--stdin"))
                {
                    return FailUsage("import-token 需要 --stdin。" );
                }

                EnsureCredentialPersistenceAvailable(store, persist);
                var token = await ReadSecretLineAsync("Token: ", cancellationToken);
                using var client = new AIHubClient(
                    settings.BaseUrl,
                    token,
                    credentials.Cookie,
                    credentials.UserAgent,
                    allowInsecureLoopback: settings.AllowInsecureLoopback,
                    cloudflareChallengeSolver: CloudflareSolver.Value);
                await client.ValidateLoginAsync(cancellationToken);
                if (persist)
                {
                    var storedCredentials = snapshot.Credentials ?? new PersistentCredentials();
                    store.Save(
                        snapshot.Settings with { PersistCredentials = true },
                        storedCredentials with { BearerToken = token });
                }

                Console.WriteLine(persist ? "Token 有效并已安全保存。" : "Token 有效，本次未保存。" );
                return 0;
            }
            default:
                return FailUsage($"未知 auth 子命令：{args[0]}");
        }
    }

    private static async Task<int> RunRouteAsync(
        AppSettingsStore store,
        string[] args,
        bool watch,
        CancellationToken cancellationToken)
    {
        var runtime = LoadRoutingRuntime(store, args);
        var json = HasFlag(args, "--json");
        var dryRun = HasFlag(args, "--dry-run");
        var auditLog = CreateAuditLog(args);

        using var profileLock = ProfileLock.TryAcquire(store.StorageDirectory);
        if (profileLock is null)
        {
            Console.Error.WriteLine("另一个 AIHubRouter 实例正在使用当前 profile。" );
            return 7;
        }

        var stateStore = new JsonRouteStateStore(store.StorageDirectory);

        if (!watch)
        {
            using var service = CreateRoutingService(store, runtime, stateStore);
            var result = await service.RunOnceAsync(dryRun, forceAccountRefresh: true, cancellationToken);
            WriteCycle(result, json, auditLog);
            if (result.Decision.Reason == RouteDecisionReason.NoCandidate)
            {
                return 6;
            }

            return result.FailedKeyCount == 0 ? 0 : 5;
        }

        return await RunWatchAsync(
            store,
            args,
            runtime,
            stateStore,
            dryRun,
            json,
            auditLog,
            cancellationToken);
    }

    private static async Task<int> RunWatchAsync(
        AppSettingsStore store,
        string[] args,
        RoutingRuntime runtime,
        IRouteStateStore stateStore,
        bool dryRun,
        bool json,
        AuditLogWriter? auditLog,
        CancellationToken cancellationToken)
    {
        using var changes = new ProfileFileChangeMonitor(store.StorageDirectory);
        var service = CreateRoutingService(store, runtime, stateStore);

        try
        {
            while (true)
            {
                try
                {
                    var result = await service.RunOnceAsync(
                        dryRun,
                        cancellationToken: cancellationToken);
                    WriteCycle(result, json, auditLog);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    WriteWatchError(exception, json, auditLog);
                }

                if (!await WaitForWatchEventAsync(
                        TimeSpan.FromSeconds(Math.Clamp(runtime.Settings.PollingIntervalSeconds, 30, 3600)),
                        changes,
                        cancellationToken))
                {
                    continue;
                }

                try
                {
                    var reloadedRuntime = LoadRoutingRuntime(store, args);
                    var reloadedService = CreateRoutingService(store, reloadedRuntime, stateStore);
                    service.Dispose();
                    service = reloadedService;
                    runtime = reloadedRuntime;
                    WriteConfigurationReloaded(runtime.Settings, json, auditLog);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    WriteConfigurationReloadError(exception, json, auditLog);
                }
            }
        }
        finally
        {
            service.Dispose();
        }
    }

    private static RoutingRuntime LoadRoutingRuntime(AppSettingsStore store, string[] args)
    {
        var snapshot = store.Load();
        return new RoutingRuntime(
            ApplyCommandOptions(ApplyEnvironmentSettings(snapshot.Settings), args),
            ApplyEnvironmentCredentials(snapshot.Credentials ?? new PersistentCredentials()));
    }

    private static RoutingService CreateRoutingService(
        AppSettingsStore store,
        RoutingRuntime runtime,
        IRouteStateStore stateStore)
    {
        return new RoutingService(
            runtime.Settings,
            runtime.Credentials,
            stateStore,
            persistCredentials: (updated, token) =>
            {
                token.ThrowIfCancellationRequested();
                var latestSnapshot = store.Load();
                if (latestSnapshot.Settings.PersistCredentials &&
                    !latestSnapshot.CredentialsUnavailable &&
                    latestSnapshot.Credentials is not null)
                {
                    var storedCredentials = latestSnapshot.Credentials;
                    store.Save(
                        latestSnapshot.Settings,
                        PreserveEnvironmentCredentials(storedCredentials, updated));
                }

                return Task.CompletedTask;
            },
            cloudflareChallengeSolver: CloudflareSolver.Value);
    }

    private static async Task<bool> WaitForWatchEventAsync(
        TimeSpan interval,
        ProfileFileChangeMonitor changes,
        CancellationToken cancellationToken)
    {
        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(interval, delayCancellation.Token);
        var changed = changes.WaitForChangeAsync(cancellationToken).AsTask();
        var completed = await Task.WhenAny(delay, changed);
        if (completed == changed)
        {
            await changed;
            delayCancellation.Cancel();
            return true;
        }

        await delay;
        return false;
    }

    private static async Task<int> RunStatusAsync(
        AppSettingsStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        var routeArgs = args.Concat(["--dry-run"]).ToArray();
        return await RunRouteAsync(store, routeArgs, watch: false, cancellationToken);
    }

    private static int RunConfig(AppSettingsStore store, string[] args)
    {
        var snapshot = store.Load();
        if (args.Length == 0 || args[0].Equals("show", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                snapshot.Settings,
                storageDirectory = store.StorageDirectory,
                canPersistCredentials = store.CanPersistCredentials,
                credentialProtection = store.CredentialProtection,
                hasStoredCredentials = snapshot.Credentials is not null
            }, JsonOptions));
            return 0;
        }

        if (!args[0].Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            return FailUsage("config 仅支持 show 或 set。" );
        }

        var updated = ApplyCommandOptions(snapshot.Settings, args[1..]);
        store.Save(updated, snapshot.Credentials);
        Console.WriteLine("配置已保存。" );
        return 0;
    }

    private static PersistentAppSettings ApplyCommandOptions(PersistentAppSettings settings, string[] args)
    {
        var baseUrl = GetOption(args, "--base-url");
        var platform = GetOption(args, "--platform");
        var mode = GetOption(args, "--mode");
        var groupStickiness = GetDoubleOption(args, "--group-stickiness");
        var minimumPrice = GetDoubleOption(args, "--min-price");
        var maximumPrice = GetDoubleOption(args, "--max-price");
        var confidenceImpact = GetDoubleOption(args, "--confidence-impact");
        var minimumConfidence = GetDoubleOption(args, "--min-confidence");
        var providerSeriesWeight = GetDoubleOption(args, "--provider-series-weight");
        var providerSeriesCache = GetIntOption(args, "--provider-series-cache");
        var providerSeriesRange = GetOption(args, "--provider-series-range");
        var providerSeriesTimezone = GetOption(args, "--provider-series-timezone");
        var interval = GetIntOption(args, "--interval");
        var selectedKeys = GetOption(args, "--selected-keys");
        var lunaSelectedKeys = GetOption(args, "--luna-selected-keys");
        var blacklistedGroups = GetOption(args, "--blacklisted-groups");
        var allowLoopback = HasFlag(args, "--allow-insecure-loopback")
            ? true
            : settings.AllowInsecureLoopback;

        if (mode is not null && !Enum.TryParse<RoutingMode>(mode, ignoreCase: true, out _))
        {
            throw new ArgumentException("--mode 必须是 economy、balanced 或 speed。" );
        }

        if (groupStickiness is < 0 || groupStickiness is { } value && !double.IsFinite(value))
        {
            throw new ArgumentException("--group-stickiness 必须是非负有限数值。");
        }

        var resolvedMinimumPrice = minimumPrice ?? settings.MinimumPriceMultiplier;
        var resolvedMaximumPrice = maximumPrice ?? settings.MaximumPriceMultiplier;
        if (resolvedMinimumPrice < 0 ||
            !double.IsFinite(resolvedMinimumPrice) ||
            !double.IsFinite(resolvedMaximumPrice) ||
            resolvedMaximumPrice < resolvedMinimumPrice)
        {
            throw new ArgumentException("价格范围必须是非负有限数值，且最小值不能大于最大值。");
        }

        var confidencePolicy = new BalancedRoutingPolicy
        {
            ConfidenceImpact = confidenceImpact ?? settings.ConfidenceImpact,
            MinimumConfidence = minimumConfidence ?? settings.MinimumConfidence
        };
        confidencePolicy.Validate();

        if (providerSeriesWeight is { } seriesWeight &&
            (seriesWeight is < 0 or > 1 || !double.IsFinite(seriesWeight)))
        {
            throw new ArgumentException("--provider-series-weight 必须是 0 到 1 之间的有限数值。");
        }

        if (providerSeriesCache is < 30 or > 3600)
        {
            throw new ArgumentException("--provider-series-cache 必须是 30 到 3600 之间的整数。");
        }

        if (providerSeriesRange is not null && string.IsNullOrWhiteSpace(providerSeriesRange))
        {
            throw new ArgumentException("--provider-series-range 不能为空。");
        }

        if (providerSeriesTimezone is not null && string.IsNullOrWhiteSpace(providerSeriesTimezone))
        {
            throw new ArgumentException("--provider-series-timezone 不能为空。");
        }

        long[]? parsedKeys = null;
        if (selectedKeys is not null)
        {
            parsedKeys = selectedKeys
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => long.TryParse(value, out var id) && id > 0
                    ? id
                    : throw new ArgumentException("--selected-keys 必须是逗号分隔的正整数。" ))
                .Distinct()
                .ToArray();
        }

        long[]? parsedLunaKeys = null;
        if (lunaSelectedKeys is not null)
        {
            parsedLunaKeys = lunaSelectedKeys
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => long.TryParse(value, out var id) && id > 0
                    ? id
                    : throw new ArgumentException("--luna-selected-keys 必须是逗号分隔的正整数。"))
                .Distinct()
                .Order()
                .ToArray();
        }

        long[]? parsedBlacklistedGroups = null;
        if (blacklistedGroups is not null)
        {
            parsedBlacklistedGroups = blacklistedGroups
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => long.TryParse(value, out var id) && id > 0
                    ? id
                    : throw new ArgumentException("--blacklisted-groups 必须是逗号分隔的正整数。"))
                .Distinct()
                .ToArray();
        }

        var resolvedSelectedKeys = parsedKeys ?? settings.SelectedKeyIds;
        var resolvedLunaKeys = parsedLunaKeys ?? settings.LunaSelectedKeyIds;
        var overlappingKeyIds = resolvedSelectedKeys
            .Intersect(resolvedLunaKeys)
            .Distinct()
            .Order()
            .ToArray();
        if (overlappingKeyIds.Length > 0)
        {
            throw new ArgumentException(
                $"主路由与 Luna 路由不能选择同一 Key：{string.Join(", ", overlappingKeyIds)}。" );
        }
        if (resolvedLunaKeys.Length > 0 && resolvedSelectedKeys.Length == 0)
        {
            throw new ArgumentException(
                "Luna 路由不能脱离主路由单独运行，请先选择主路由 Key。" );
        }

        return settings with
        {
            BaseUrl = baseUrl ?? settings.BaseUrl,
            Platform = platform ?? settings.Platform,
            RoutingMode = mode is null
                ? settings.RoutingMode
                : Enum.Parse<RoutingMode>(mode, ignoreCase: true),
            GroupStickiness = groupStickiness ?? settings.GroupStickiness,
            MinimumPriceMultiplier = resolvedMinimumPrice,
            MaximumPriceMultiplier = resolvedMaximumPrice,
            ConfidenceImpact = confidencePolicy.ConfidenceImpact,
            MinimumConfidence = confidencePolicy.MinimumConfidence,
            ProviderSeriesWeight = providerSeriesWeight ?? settings.ProviderSeriesWeight,
            ProviderSeriesCacheSeconds = providerSeriesCache ?? settings.ProviderSeriesCacheSeconds,
            ProviderSeriesRange = providerSeriesRange?.Trim() ?? settings.ProviderSeriesRange,
            ProviderSeriesTimezone = providerSeriesTimezone?.Trim() ?? settings.ProviderSeriesTimezone,
            PollingIntervalSeconds = interval is null
                ? settings.PollingIntervalSeconds
                : Math.Clamp(interval.Value, 30, 3600),
            AllowInsecureLoopback = allowLoopback,
            KeySelectionInitialized = parsedKeys is not null ||
                parsedLunaKeys is not null ||
                settings.KeySelectionInitialized,
            SelectedKeyIds = parsedKeys ?? settings.SelectedKeyIds,
            LunaSelectedKeyIds = parsedLunaKeys ?? settings.LunaSelectedKeyIds,
            BlacklistedGroupIds = parsedBlacklistedGroups ?? settings.BlacklistedGroupIds
        };
    }

    private static PersistentAppSettings ApplyEnvironmentSettings(PersistentAppSettings settings)
    {
        var baseUrl = Environment.GetEnvironmentVariable("AIHUB_BASE_URL");
        return string.IsNullOrWhiteSpace(baseUrl) ? settings : settings with { BaseUrl = baseUrl };
    }

    private static PersistentCredentials ApplyEnvironmentCredentials(PersistentCredentials credentials)
    {
        return credentials with
        {
            Email = Environment.GetEnvironmentVariable("AIHUB_EMAIL") ?? credentials.Email,
            Password = Environment.GetEnvironmentVariable("AIHUB_PASSWORD") ?? credentials.Password,
            BearerToken = Environment.GetEnvironmentVariable("AIHUB_TOKEN") ?? credentials.BearerToken,
            RefreshToken = Environment.GetEnvironmentVariable("AIHUB_REFRESH_TOKEN") ?? credentials.RefreshToken,
            Cookie = Environment.GetEnvironmentVariable("AIHUB_COOKIE") ?? credentials.Cookie,
            UserAgent = Environment.GetEnvironmentVariable("AIHUB_USER_AGENT") ?? credentials.UserAgent
        };
    }

    private static PersistentCredentials PreserveEnvironmentCredentials(
        PersistentCredentials stored,
        PersistentCredentials effective)
    {
        var runtimeTokenChainOverride = HasEnvironmentVariable("AIHUB_PASSWORD") ||
            HasEnvironmentVariable("AIHUB_TOKEN") ||
            HasEnvironmentVariable("AIHUB_REFRESH_TOKEN") ||
            HasEnvironmentVariable("AIHUB_COOKIE");
        return effective with
        {
            Email = Environment.GetEnvironmentVariable("AIHUB_EMAIL") is null
                ? effective.Email
                : stored.Email,
            Password = Environment.GetEnvironmentVariable("AIHUB_PASSWORD") is null
                ? effective.Password
                : stored.Password,
            BearerToken = runtimeTokenChainOverride ? stored.BearerToken : effective.BearerToken,
            RefreshToken = runtimeTokenChainOverride ? stored.RefreshToken : effective.RefreshToken,
            AccessTokenExpiresAt = runtimeTokenChainOverride
                ? stored.AccessTokenExpiresAt
                : effective.AccessTokenExpiresAt,
            Cookie = Environment.GetEnvironmentVariable("AIHUB_COOKIE") is null
                ? effective.Cookie
                : stored.Cookie,
            UserAgent = Environment.GetEnvironmentVariable("AIHUB_USER_AGENT") is null
                ? effective.UserAgent
                : stored.UserAgent
        };
    }

    private static bool HasEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name) is not null;

    private static void EnsureCredentialPersistenceAvailable(
        AppSettingsStore store,
        bool persist)
    {
        if (persist && !store.CanPersistCredentials)
        {
            throw new InvalidOperationException(store.CredentialProtection);
        }
    }

    private static void WriteCycle(RoutingCycleResult result, bool json, AuditLogWriter? auditLog)
    {
        var payload = BuildCyclePayload(result);
        WriteAudit(auditLog, payload);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
            return;
        }

        var decision = result.Decision;
        if (decision.Target is null)
        {
            Console.WriteLine(
                $"[{result.CompletedAt:O}] 无可用路由。" +
                $"序列：{result.ProviderSeriesStatus.Message} " +
                $"缓存命中率：{result.ProviderCacheHitRateStatus.Message}" +
                FormatLunaCycleSummary(result.LunaRoute));
            return;
        }

        Console.WriteLine(
            $"[{result.CompletedAt:O}] {decision.Reason}: " +
            $"group={decision.Target.Group.Id} ({decision.Target.Group.Name}), " +
            $"rate={decision.Target.EffectiveMultiplier:0.####}x, " +
            $"first-token={FormatLatency(decision.Target.Provider.FirstTokenLatencyMs)}, " +
            $"switch={decision.ShouldSwitch}, changed={result.ChangedKeyCount}, failed={result.FailedKeyCount}, " +
            $"provider-series={result.ProviderSeriesStatus.Message}, " +
            $"cache-hit-rate={result.ProviderCacheHitRateStatus.Message}" +
            FormatLunaCycleSummary(result.LunaRoute));
    }

    private static string FormatLunaCycleSummary(LunaRouteResult? lunaRoute)
    {
        if (lunaRoute is null)
        {
            return string.Empty;
        }

        var target = lunaRoute.Decision?.Target;
        var targetText = target is null
            ? "无可用候选"
            : $"{target.Group.Id} ({target.Group.Name})";
        var health = lunaRoute.HealthAvailable ? "可用" : "不可用";
        return $", luna-target={targetText}, luna-filtered={lunaRoute.FilteredGroupCount}, " +
            $"luna-health={health} ({lunaRoute.HealthMessage})";
    }

    private static object BuildCyclePayload(RoutingCycleResult result)
    {
        var evaluation = result.Evaluation;
        var tradeoffGroups = evaluation.TradeoffCandidates
            .Select(candidate => candidate.Group.Id)
            .ToHashSet();
        return new
        {
            schemaVersion = 3,
            eventType = "routingCycle",
            processId = Environment.ProcessId,
            result.DryRun,
            result.CompletedAt,
            policy = new
            {
                evaluation.PriceWeight,
                evaluation.LatencyWeight,
                evaluation.MinimumMultiplier,
                baselineGroupId = evaluation.Baseline?.Group.Id
            },
            candidates = evaluation.EligibleCandidates
                .OrderBy(candidate => candidate.EffectiveMultiplier)
                .ThenBy(candidate => candidate.Provider.FirstTokenLatencyMs ?? double.MaxValue)
                .Select(candidate => new
                {
                    groupId = candidate.Group.Id,
                    groupName = candidate.Group.Name,
                    multiplier = candidate.EffectiveMultiplier,
                    firstTokenLatencyMs = candidate.Provider.FirstTokenLatencyMs,
                    latencyConfidence = candidate.Provider.LatencyConfidence,
                    cacheHitRate = candidate.Provider.CacheHitRate,
                    usageSampleCount = candidate.Provider.UsageSampleCount,
                    lastSampleAt = candidate.Provider.CheckedAt,
                    pricePremiumPercent = CalculatePricePremium(evaluation, candidate),
                    speedupRatio = CalculateSpeedupRatio(evaluation, candidate),
                    weightedScore = CalculateWeightedScore(evaluation, candidate),
                    tradeoffEligible = tradeoffGroups.Contains(candidate.Group.Id),
                    recommended = candidate.Group.Id == evaluation.Recommended?.Group.Id
                }),
            decision = new
            {
                reason = result.Decision.Reason.ToString(),
                result.Decision.ShouldSwitch,
                currentGroupId = result.Decision.Current?.Group.Id,
                targetGroupId = result.Decision.Target?.Group.Id,
                targetGroupName = result.Decision.Target?.Group.Name,
                multiplier = result.Decision.Target?.EffectiveMultiplier,
                firstTokenLatencyMs = result.Decision.Target?.Provider.FirstTokenLatencyMs,
                latencyConfidence = result.Decision.Target?.Provider.LatencyConfidence,
                cacheHitRate = result.Decision.Target?.Provider.CacheHitRate,
                usageSampleCount = result.Decision.Target?.Provider.UsageSampleCount,
                lastSampleAt = result.Decision.Target?.Provider.CheckedAt,
                result.Decision.PricePremiumPercent,
                result.Decision.LatencyImprovementPercent
            },
            result.SelectedKeyIds,
            result.KeyResults,
            providerSeriesStatus = new
            {
                result.ProviderSeriesStatus.Available,
                result.ProviderSeriesStatus.FromCache,
                result.ProviderSeriesStatus.IsDegraded,
                result.ProviderSeriesStatus.Message
            },
            providerCacheHitRateStatus = new
            {
                result.ProviderCacheHitRateStatus.Available,
                result.ProviderCacheHitRateStatus.FromCache,
                result.ProviderCacheHitRateStatus.IsDegraded,
                result.ProviderCacheHitRateStatus.Message
            },
            result.ChangedKeyCount,
            result.FailedKeyCount,
            lunaRoute = result.LunaRoute is { } lunaRoute
                ? new
                {
                    selectedKeyIds = lunaRoute.SelectedKeyIds,
                    decision = lunaRoute.Decision is { } decision
                        ? new
                        {
                            reason = decision.Reason.ToString(),
                            decision.ShouldSwitch,
                            currentGroupId = decision.Current?.Group.Id,
                            targetGroupId = decision.Target?.Group.Id,
                            targetGroupName = decision.Target?.Group.Name,
                            multiplier = decision.Target?.EffectiveMultiplier,
                            firstTokenLatencyMs = decision.Target?.Provider.FirstTokenLatencyMs,
                            latencyConfidence = decision.Target?.Provider.LatencyConfidence,
                            cacheHitRate = decision.Target?.Provider.CacheHitRate,
                            usageSampleCount = decision.Target?.Provider.UsageSampleCount,
                            lastSampleAt = decision.Target?.Provider.CheckedAt,
                            decision.PricePremiumPercent,
                            decision.LatencyImprovementPercent
                        }
                        : null,
                    filteredGroupCount = lunaRoute.FilteredGroupCount,
                    healthAvailable = lunaRoute.HealthAvailable,
                    healthMessage = lunaRoute.HealthMessage,
                    lunaRoute.KeyResults
                }
                : null
        };
    }

    private static void WriteWatchError(Exception exception, bool json, AuditLogWriter? auditLog)
    {
        var message = exception switch
        {
            AIHubApiException api => api.Message,
            HttpRequestException => "网络连接失败。",
            _ => exception.Message
        };
        var payload = new
        {
            schemaVersion = 2,
            eventType = "routingError",
            processId = Environment.ProcessId,
            timestamp = DateTimeOffset.UtcNow,
            error = message
        };
        WriteAudit(auditLog, payload);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
        }
        else
        {
            Console.Error.WriteLine($"[{DateTimeOffset.UtcNow:O}] {message}" );
        }
    }

    private static void WriteConfigurationReloaded(
        PersistentAppSettings settings,
        bool json,
        AuditLogWriter? auditLog)
    {
        var payload = new
        {
            schemaVersion = 2,
            eventType = "configurationReloaded",
            processId = Environment.ProcessId,
            timestamp = DateTimeOffset.UtcNow,
            pollingIntervalSeconds = settings.PollingIntervalSeconds,
            routingMode = settings.RoutingMode.ToString(),
            groupStickiness = settings.CreatePolicy().MinimumScoreAdvantageToSwitch,
            minimumPriceMultiplier = settings.MinimumPriceMultiplier,
            maximumPriceMultiplier = settings.MaximumPriceMultiplier,
            confidenceImpact = settings.ConfidenceImpact,
            minimumConfidence = settings.MinimumConfidence,
            blacklistedGroupCount = settings.BlacklistedGroupIds.Length,
            selectedKeyCount = settings.SelectedKeyIds.Length,
            lunaSelectedKeyCount = settings.LunaSelectedKeyIds.Length
        };
        WriteAudit(auditLog, payload);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
        }
        else
        {
            Console.Error.WriteLine(
                $"[{DateTimeOffset.UtcNow:O}] 配置已热重载，轮询间隔：{settings.PollingIntervalSeconds} 秒。" );
        }
    }

    private static void WriteConfigurationReloadError(
        Exception exception,
        bool json,
        AuditLogWriter? auditLog)
    {
        var payload = new
        {
            schemaVersion = 2,
            eventType = "configurationReloadError",
            processId = Environment.ProcessId,
            timestamp = DateTimeOffset.UtcNow,
            error = exception.Message
        };
        WriteAudit(auditLog, payload);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
        }
        else
        {
            Console.Error.WriteLine(
                $"[{DateTimeOffset.UtcNow:O}] 配置热重载失败，继续使用当前配置：{exception.Message}" );
        }
    }

    private static string FormatLatency(double? latency) =>
        latency is >= 0 && double.IsFinite(latency.Value) ? $"{latency:0}ms" : "unknown";

    private static AuditLogWriter? CreateAuditLog(string[] args)
    {
        var path = GetOption(args, "--log-file");
        if (path is null)
        {
            return null;
        }

        return new AuditLogWriter(
            path,
            GetIntOption(args, "--log-max-mb") ?? 20,
            GetIntOption(args, "--log-files") ?? 7);
    }

    private static void WriteAudit(AuditLogWriter? auditLog, object payload)
    {
        if (auditLog is null)
        {
            return;
        }

        try
        {
            auditLog.Write(payload);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"审计日志写入失败：{exception.Message}");
        }
    }

    private static double? CalculatePricePremium(RouteEvaluation evaluation, RouteCandidate candidate)
    {
        if (evaluation.MinimumMultiplier is not > 0)
        {
            return candidate.EffectiveMultiplier == 0 ? 0 : null;
        }

        return (candidate.EffectiveMultiplier - evaluation.MinimumMultiplier.Value) /
            evaluation.MinimumMultiplier.Value * 100;
    }

    private static double? CalculateSpeedupRatio(
        RouteEvaluation evaluation,
        RouteCandidate candidate)
    {
        var baseline = evaluation.Baseline?.Provider.FirstTokenLatencyMs;
        var latency = candidate.Provider.FirstTokenLatencyMs;
        if (baseline is not > 0 || latency is not > 0 || !double.IsFinite(latency.Value))
        {
            return null;
        }

        var baselineConfidence = evaluation.Baseline?.Provider.LatencyConfidence;
        var candidateConfidence = candidate.Provider.LatencyConfidence;
        var baselinePenalty = baselineConfidence is { } baseValue && double.IsFinite(baseValue)
            ? 1 + Math.Clamp(1 - baseValue, 0, 1)
            : 2;
        var candidatePenalty = candidateConfidence is { } candidateValue && double.IsFinite(candidateValue)
            ? 1 + Math.Clamp(1 - candidateValue, 0, 1)
            : 2;
        return baseline.Value * baselinePenalty / (latency.Value * candidatePenalty) - 1;
    }

    private static double? CalculateWeightedScore(RouteEvaluation evaluation, RouteCandidate candidate)
    {
        var premium = CalculatePricePremium(evaluation, candidate);
        var speedupRatio = CalculateSpeedupRatio(evaluation, candidate);
        return premium is null || speedupRatio is null
            ? null
            : evaluation.LatencyWeight * speedupRatio.Value -
                evaluation.PriceWeight * (premium.Value / 100);
    }

    private static async Task<string> ReadSecretLineAsync(
        string prompt,
        CancellationToken cancellationToken)
    {
        if (!Console.IsInputRedirected)
        {
            Console.Error.Write(prompt);
        }

        var value = await Console.In.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("stdin 中没有有效的凭据。" );
        }

        return value.TrimEnd('\r', '\n');
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"{name} 缺少值。" );
            }

            return args[index + 1];
        }

        return null;
    }

    private static int? GetIntOption(string[] args, string name)
    {
        var value = GetOption(args, name);
        if (value is null)
        {
            return null;
        }

        return int.TryParse(value, out var result)
            ? result
            : throw new ArgumentException($"{name} 必须是整数。" );
    }

    private static double? GetDoubleOption(string[] args, string name)
    {
        var value = GetOption(args, name);
        if (value is null)
        {
            return null;
        }

        return double.TryParse(
            value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var result)
            ? result
            : throw new ArgumentException($"{name} 必须是数值。");
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(value => value.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool IsHelp(string value) =>
        value is "help" or "--help" or "-h";

    private static int FailUsage(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine("运行 aihub-router --help 查看用法。" );
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            AIHubRouter API-only cross-platform router

            Usage:
              aihub-router auth login --email <email> --password-stdin [--persist|--no-persist]
              aihub-router auth import-token --stdin [--persist|--no-persist]
              aihub-router route --once [--dry-run] [--json]
              aihub-router watch [--interval <seconds>] [--dry-run] [--json]
              aihub-router status [--json]
              aihub-router config show
              aihub-router config set [options]

            Configuration options:
              --base-url <https-url>
              --platform <openai|anthropic|gemini|antigravity|grok>
              --mode <economy|balanced|speed>
              --group-stickiness <non-negative-number>
              --min-price <non-negative-number>
              --max-price <non-negative-number>
              --confidence-impact <0-2>
              --min-confidence <0-1>
              --provider-series-weight <0-1>  (供应商参考权重)
              --provider-series-cache <30-3600>  (序列响应缓存秒数)
              --provider-series-range <non-empty-value>
              --provider-series-timezone <non-empty-value>
              --interval <30-3600>
              --selected-keys <id,id,...>
              --luna-selected-keys <id,id,...>
              --blacklisted-groups <id,id,...>

            Audit options for route/watch:
              --log-file <path>
              --log-max-mb <1-1024>
              --log-files <1-30>

            Credential environment variables:
              AIHUB_EMAIL, AIHUB_PASSWORD, AIHUB_TOKEN, AIHUB_REFRESH_TOKEN,
              AIHUB_COOKIE, AIHUB_USER_AGENT, AIHUB_ROUTER_MASTER_KEY

            No command opens or embeds a browser. Passwords and tokens are not accepted
            as regular command-line option values.
            """ );
    }

    private sealed record RoutingRuntime(
        PersistentAppSettings Settings,
        PersistentCredentials Credentials);
}
