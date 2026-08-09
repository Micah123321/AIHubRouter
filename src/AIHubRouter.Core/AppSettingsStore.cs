using System.Security.Cryptography;
using System.Text.Json;

namespace AIHubRouter.Core;

public enum AppThemeMode
{
    System,
    Light,
    Dark
}

public sealed record PersistentAppSettings
{
    public bool PersistCredentials { get; init; } = true;
    public string BaseUrl { get; init; } = "https://aihub.top";
    public bool AllowInsecureLoopback { get; init; }
    public string Platform { get; init; } = "openai";
    public RoutingMode RoutingMode { get; init; } = RoutingMode.Balanced;
    public double? GroupStickiness { get; init; }
    public double MinimumPriceMultiplier { get; init; } = BalancedRoutingPolicy.DefaultMinimumPriceMultiplier;
    public double MaximumPriceMultiplier { get; init; } = BalancedRoutingPolicy.DefaultMaximumPriceMultiplier;
    public double ConfidenceImpact { get; init; } = BalancedRoutingPolicy.DefaultConfidenceImpact;
    public double MinimumConfidence { get; init; } = BalancedRoutingPolicy.DefaultMinimumConfidence;
    public double ProviderSeriesWeight { get; init; } = BalancedRoutingPolicy.DefaultProviderSeriesWeight;
    public int ProviderSeriesCacheSeconds { get; init; } = 300;
    public string ProviderSeriesRange { get; init; } = "6h";
    public string ProviderSeriesTimezone { get; init; } = "Asia/Shanghai";
    public int PollingIntervalSeconds { get; init; } = 60;
    public int AccountCacheSeconds { get; init; } = 300;
    public bool SmoothRendering { get; init; } = true;
    public AppThemeMode ThemeMode { get; init; } = AppThemeMode.System;
    public bool AutoRoutingEnabled { get; init; }
    public bool KeySelectionInitialized { get; init; }
    public long[] SelectedKeyIds { get; init; } = [];
    public long[] LunaSelectedKeyIds { get; init; } = [];
    public long[] BlacklistedGroupIds { get; init; } = [];
    public bool ReliabilityDetectionEnabled { get; init; } = true;
    public int ReliabilityDetectionIntervalSeconds { get; init; } = 600;
    public int ReliabilityQuarantineHours { get; init; } = 24;
    public string DetectorPythonCommand { get; init; } = "python3";
    public string DetectorWorkerPath { get; init; } = "scripts/channel_detector_worker.py";
    public string DetectorPreset { get; init; } = "low";
    public DetectorBinding[] DetectorBindings { get; init; } = [];

    public BalancedRoutingPolicy CreatePolicy()
    {
        return new BalancedRoutingPolicy
        {
            Platform = string.IsNullOrWhiteSpace(Platform) ? "openai" : Platform,
            Mode = RoutingMode,
            MinimumScoreAdvantageOverride = GroupStickiness,
            MinimumPriceMultiplier = MinimumPriceMultiplier,
            MaximumPriceMultiplier = MaximumPriceMultiplier,
            ConfidenceImpact = ConfidenceImpact,
            MinimumConfidence = MinimumConfidence,
            ProviderSeriesWeight = ProviderSeriesWeight,
            MaximumStatusAge = TimeSpan.FromMinutes(15),
            BlacklistedGroupIds = BlacklistedGroupIds
                .Where(groupId => groupId > 0)
                .Distinct()
                .ToArray()
        };
    }
}

public sealed record PersistentCredentials
{
    public string Email { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string BearerToken { get; init; } = string.Empty;
    public string RefreshToken { get; init; } = string.Empty;
    public DateTimeOffset? AccessTokenExpiresAt { get; init; }
    public string Cookie { get; init; } = string.Empty;
    public string UserAgent { get; init; } = string.Empty;
    public Dictionary<long, string> DetectorApiKeys { get; init; } = [];
}

public sealed record PersistenceSnapshot(
    PersistentAppSettings Settings,
    PersistentCredentials? Credentials)
{
    public bool CredentialsUnavailable { get; init; }
}

public interface ICredentialProtector
{
    bool IsAvailable { get; }
    string Description { get; }
    byte[] Protect(ReadOnlySpan<byte> plaintext);
    byte[] Unprotect(ReadOnlySpan<byte> encrypted);
}

public sealed partial class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    // ha-min: 全局进程内持久化锁，当前 profile 数量有限；多 profile 并发时升级为按 storageDirectory 分片锁。
    private static readonly object PersistenceGate = new();

    private readonly string _storageDirectory;
    private readonly string _settingsPath;
    private readonly string _credentialsPath;
    private readonly string _transactionPath;
    private readonly string _persistenceLockPath;
    private readonly ICredentialProtector _credentialProtector;

    public AppSettingsStore(
        string? storageDirectory = null,
        ICredentialProtector? credentialProtector = null)
    {
        _storageDirectory = storageDirectory ?? AppPaths.GetConfigurationDirectory();
        _settingsPath = Path.Combine(_storageDirectory, "settings.json");
        _credentialsPath = Path.Combine(_storageDirectory, "credentials.dat");
        _transactionPath = Path.Combine(_storageDirectory, "persistence.transaction.json");
        _persistenceLockPath = Path.Combine(_storageDirectory, "persistence.lock");
        _credentialProtector = credentialProtector ?? CredentialProtectorFactory.CreateDefault();
    }

    public string StorageDirectory => _storageDirectory;
    public string CredentialProtection => _credentialProtector.Description;
    public bool CanPersistCredentials => _credentialProtector.IsAvailable;

    public PersistenceSnapshot Load()
    {
        lock (PersistenceGate)
        {
            using var persistenceLock = AcquirePersistenceLock();
            RecoverPendingTransaction();
            var settings = File.Exists(_settingsPath)
                ? JsonSerializer.Deserialize<PersistentAppSettings>(File.ReadAllText(_settingsPath), JsonOptions)
                    ?? new PersistentAppSettings()
                : new PersistentAppSettings();
            settings = settings with
            {
                ProviderSeriesRange = string.IsNullOrWhiteSpace(settings.ProviderSeriesRange)
                    ? "6h"
                    : settings.ProviderSeriesRange.Trim(),
                ProviderSeriesTimezone = string.IsNullOrWhiteSpace(settings.ProviderSeriesTimezone)
                    ? "Asia/Shanghai"
                    : settings.ProviderSeriesTimezone.Trim(),
                SelectedKeyIds = settings.SelectedKeyIds ?? [],
                LunaSelectedKeyIds = settings.LunaSelectedKeyIds ?? [],
                BlacklistedGroupIds = settings.BlacklistedGroupIds ?? [],
                DetectorBindings = settings.DetectorBindings ?? []
            };
            PersistentCredentials? credentials = null;
            var credentialsUnavailable = false;
            if (settings.PersistCredentials && File.Exists(_credentialsPath))
            {
                if (_credentialProtector.IsAvailable)
                {
                    var encrypted = File.ReadAllBytes(_credentialsPath);
                    var plaintext = _credentialProtector.Unprotect(encrypted);
                    try
                    {
                        credentials = JsonSerializer.Deserialize<PersistentCredentials>(plaintext, JsonOptions);
                        if (credentials is not null)
                        {
                            credentials = credentials with
                            {
                                DetectorApiKeys = credentials.DetectorApiKeys ?? []
                            };
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(plaintext);
                    }
                }
                else
                {
                    credentialsUnavailable = true;
                }
            }

            return new PersistenceSnapshot(settings, credentials)
            {
                CredentialsUnavailable = credentialsUnavailable
            };
        }
    }

    public void Save(PersistentAppSettings settings, PersistentCredentials? credentials)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (PersistenceGate)
        {
            using var persistenceLock = AcquirePersistenceLock();
            RecoverPendingTransaction();
            var hasCredentials = credentials is not null && HasCredentialValues(credentials);
            if (settings.PersistCredentials && hasCredentials && !_credentialProtector.IsAvailable)
            {
                throw new InvalidOperationException(
                    "当前环境没有可用的安全凭据存储。请使用环境变量或提供 AIHUB_ROUTER_MASTER_KEY。" );
            }

            byte[]? encryptedCredentials = null;
            byte[]? plaintext = null;
            try
            {
                if (settings.PersistCredentials && hasCredentials)
                {
                    plaintext = JsonSerializer.SerializeToUtf8Bytes(credentials, JsonOptions);
                    encryptedCredentials = _credentialProtector.Protect(plaintext);
                }

                var removeCredentials = !settings.PersistCredentials || credentials is not null && !hasCredentials;
                CommitFiles(
                    JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions),
                    encryptedCredentials,
                    removeCredentials);
            }
            finally
            {
                if (plaintext is not null)
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }

                if (encryptedCredentials is not null)
                {
                    CryptographicOperations.ZeroMemory(encryptedCredentials);
                }
            }
        }
    }

    public void ClearCredentials()
    {
        lock (PersistenceGate)
        {
            using var persistenceLock = AcquirePersistenceLock();
            RecoverPendingTransaction();
            DeleteIfExists(_credentialsPath);
        }
    }

    private static bool HasCredentialValues(PersistentCredentials credentials) =>
        !string.IsNullOrWhiteSpace(credentials.Email) ||
        !string.IsNullOrWhiteSpace(credentials.Password) ||
        !string.IsNullOrWhiteSpace(credentials.BearerToken) ||
        !string.IsNullOrWhiteSpace(credentials.RefreshToken) ||
        credentials.AccessTokenExpiresAt is not null ||
        !string.IsNullOrWhiteSpace(credentials.Cookie) ||
        !string.IsNullOrWhiteSpace(credentials.UserAgent) ||
        (credentials.DetectorApiKeys ?? []).Any(pair =>
            pair.Key > 0 && !string.IsNullOrWhiteSpace(pair.Value));

    private void EnsureStorageDirectory()
    {
        Directory.CreateDirectory(_storageDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                _storageDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private FileStream AcquirePersistenceLock()
    {
        EnsureStorageDirectory();
        return new FileStream(
            _persistenceLockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.DeleteOnClose);
    }

}

public static class AppPaths
{
    public static string GetConfigurationDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AIHubRouter");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Application Support", "AIHubRouter");
        }

        var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return Path.Combine(
            string.IsNullOrWhiteSpace(xdgConfigHome) ? Path.Combine(home, ".config") : xdgConfigHome,
            "AIHubRouter");
    }
}
