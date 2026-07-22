using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHubRouter.Core;

namespace AIHubRouter.Cli;

internal static class CliApplication
{
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

        var snapshot = store.Load();
        var settings = ApplyEnvironmentSettings(snapshot.Settings);
        var persist = HasFlag(args, "--persist");
        switch (args[0].ToLowerInvariant())
        {
            case "login":
            {
                var email = GetOption(args, "--email") ?? Environment.GetEnvironmentVariable("AIHUB_EMAIL");
                if (string.IsNullOrWhiteSpace(email) || !HasFlag(args, "--password-stdin"))
                {
                    return FailUsage("login 需要 --email 和 --password-stdin。" );
                }

                var password = await ReadSecretLineAsync("Password: ", cancellationToken);
                using var client = new AIHubClient(
                    settings.BaseUrl,
                    allowInsecureLoopback: settings.AllowInsecureLoopback);
                var session = await client.LoginAsync(
                    new LoginCredentials(email, password),
                    cancellationToken);
                var credentials = new PersistentCredentials
                {
                    Email = email.Trim(),
                    Password = password,
                    BearerToken = session.AccessToken,
                    RefreshToken = session.RefreshToken,
                    AccessTokenExpiresAt = session.ExpiresAt
                };
                try
                {
                    if (persist)
                    {
                        if (!store.CanPersistCredentials)
                        {
                            throw new InvalidOperationException(store.CredentialProtection);
                        }

                        store.Save(settings with { PersistCredentials = true }, credentials);
                    }

                    Console.WriteLine(persist
                        ? $"登录成功，session 已安全保存，有效期至 {session.ExpiresAt:O}。"
                        : $"登录成功，session 未保存，有效期至 {session.ExpiresAt:O}。" );
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

                var token = await ReadSecretLineAsync("Token: ", cancellationToken);
                using var client = new AIHubClient(
                    settings.BaseUrl,
                    token,
                    allowInsecureLoopback: settings.AllowInsecureLoopback);
                await client.ValidateLoginAsync(cancellationToken);
                if (persist)
                {
                    if (!store.CanPersistCredentials)
                    {
                        throw new InvalidOperationException(store.CredentialProtection);
                    }

                    store.Save(
                        settings with { PersistCredentials = true },
                        new PersistentCredentials { BearerToken = token });
                }

                Console.WriteLine(persist ? "Token 有效并已安全保存。" : "Token 有效，但未保存。" );
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
        var snapshot = store.Load();
        var settings = ApplyCommandOptions(ApplyEnvironmentSettings(snapshot.Settings), args);
        var credentials = ApplyEnvironmentCredentials(snapshot.Credentials ?? new PersistentCredentials());
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
        using var service = new RoutingService(
            settings,
            credentials,
            stateStore,
            persistCredentials: (updated, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (settings.PersistCredentials)
                {
                    store.Save(settings, updated);
                }

                return Task.CompletedTask;
            });

        if (!watch)
        {
            var result = await service.RunOnceAsync(dryRun, forceAccountRefresh: true, cancellationToken);
            WriteCycle(result, json, auditLog);
            if (result.Decision.Reason == RouteDecisionReason.NoCandidate)
            {
                return 6;
            }

            return result.FailedKeyCount == 0 ? 0 : 5;
        }

        var interval = TimeSpan.FromSeconds(Math.Clamp(settings.PollingIntervalSeconds, 30, 3600));
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                var result = await service.RunOnceAsync(dryRun, cancellationToken: cancellationToken);
                WriteCycle(result, json, auditLog);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                WriteWatchError(exception, json, auditLog);
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));

        return 0;
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
        var interval = GetIntOption(args, "--interval");
        var selectedKeys = GetOption(args, "--selected-keys");
        var blacklistedGroups = GetOption(args, "--blacklisted-groups");
        var allowLoopback = HasFlag(args, "--allow-insecure-loopback")
            ? true
            : settings.AllowInsecureLoopback;

        if (mode is not null && !Enum.TryParse<RoutingMode>(mode, ignoreCase: true, out _))
        {
            throw new ArgumentException("--mode 必须是 economy、balanced 或 speed。" );
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

        return settings with
        {
            BaseUrl = baseUrl ?? settings.BaseUrl,
            Platform = platform ?? settings.Platform,
            RoutingMode = mode is null
                ? settings.RoutingMode
                : Enum.Parse<RoutingMode>(mode, ignoreCase: true),
            PollingIntervalSeconds = interval is null
                ? settings.PollingIntervalSeconds
                : Math.Clamp(interval.Value, 30, 3600),
            AllowInsecureLoopback = allowLoopback,
            KeySelectionInitialized = parsedKeys is null
                ? settings.KeySelectionInitialized
                : true,
            SelectedKeyIds = parsedKeys ?? settings.SelectedKeyIds,
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
            Console.WriteLine($"[{result.CompletedAt:O}] 无可用路由。" );
            return;
        }

        Console.WriteLine(
            $"[{result.CompletedAt:O}] {decision.Reason}: " +
            $"group={decision.Target.Group.Id} ({decision.Target.Group.Name}), " +
            $"rate={decision.Target.EffectiveMultiplier:0.####}x, " +
            $"first-token={FormatLatency(decision.Target.Provider.FirstTokenLatencyMs)}, " +
            $"switch={decision.ShouldSwitch}, changed={result.ChangedKeyCount}, failed={result.FailedKeyCount}" );
    }

    private static object BuildCyclePayload(RoutingCycleResult result)
    {
        var evaluation = result.Evaluation;
        var tradeoffGroups = evaluation.TradeoffCandidates
            .Select(candidate => candidate.Group.Id)
            .ToHashSet();
        return new
        {
            schemaVersion = 2,
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
                    successRate6h = candidate.Provider.SuccessRate6h,
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
                result.Decision.PricePremiumPercent,
                result.Decision.LatencyImprovementPercent
            },
            result.SelectedKeyIds,
            result.KeyResults,
            result.ChangedKeyCount,
            result.FailedKeyCount
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

        return baseline.Value / latency.Value - 1;
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
              aihub-router auth login --email <email> --password-stdin [--persist]
              aihub-router auth import-token --stdin [--persist]
              aihub-router route --once [--dry-run] [--json]
              aihub-router watch [--interval <seconds>] [--dry-run] [--json]
              aihub-router status [--json]
              aihub-router config show
              aihub-router config set [options]

            Configuration options:
              --base-url <https-url>
              --platform <openai|anthropic|gemini|antigravity|grok>
              --mode <economy|balanced|speed>
              --interval <30-3600>
              --selected-keys <id,id,...>
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
}
