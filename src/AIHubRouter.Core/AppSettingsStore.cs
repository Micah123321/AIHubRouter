using System.ComponentModel;
using System.Runtime.InteropServices;
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
    public bool PersistCredentials { get; init; }
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
}

public sealed record PersistenceSnapshot(
    PersistentAppSettings Settings,
    PersistentCredentials? Credentials);

public interface ICredentialProtector
{
    bool IsAvailable { get; }
    string Description { get; }
    byte[] Protect(ReadOnlySpan<byte> plaintext);
    byte[] Unprotect(ReadOnlySpan<byte> encrypted);
}

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _storageDirectory;
    private readonly string _settingsPath;
    private readonly string _credentialsPath;
    private readonly ICredentialProtector _credentialProtector;

    public AppSettingsStore(
        string? storageDirectory = null,
        ICredentialProtector? credentialProtector = null)
    {
        _storageDirectory = storageDirectory ?? AppPaths.GetConfigurationDirectory();
        _settingsPath = Path.Combine(_storageDirectory, "settings.json");
        _credentialsPath = Path.Combine(_storageDirectory, "credentials.dat");
        _credentialProtector = credentialProtector ?? CredentialProtectorFactory.CreateDefault();
    }

    public string StorageDirectory => _storageDirectory;
    public string CredentialProtection => _credentialProtector.Description;
    public bool CanPersistCredentials => _credentialProtector.IsAvailable;

    public PersistenceSnapshot Load()
    {
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
                : settings.ProviderSeriesTimezone.Trim()
        };
        PersistentCredentials? credentials = null;
        if (settings.PersistCredentials && File.Exists(_credentialsPath))
        {
            if (_credentialProtector.IsAvailable)
            {
                var encrypted = File.ReadAllBytes(_credentialsPath);
                var plaintext = _credentialProtector.Unprotect(encrypted);
                try
                {
                    credentials = JsonSerializer.Deserialize<PersistentCredentials>(plaintext, JsonOptions);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
        }

        return new PersistenceSnapshot(settings, credentials);
    }

    public void Save(PersistentAppSettings settings, PersistentCredentials? credentials)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.PersistCredentials && credentials is not null && !_credentialProtector.IsAvailable)
        {
            throw new InvalidOperationException(
                "当前环境没有可用的安全凭据存储。请使用环境变量或提供 AIHUB_ROUTER_MASTER_KEY。" );
        }

        EnsureStorageDirectory();
        WriteAtomically(_settingsPath, JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions));

        if (!settings.PersistCredentials)
        {
            ClearCredentials();
            return;
        }

        if (credentials is null)
        {
            return;
        }

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(credentials, JsonOptions);
        try
        {
            var encrypted = _credentialProtector.Protect(plaintext);
            try
            {
                WriteAtomically(_credentialsPath, encrypted);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public void ClearCredentials()
    {
        if (File.Exists(_credentialsPath))
        {
            File.Delete(_credentialsPath);
        }
    }

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

    private static void WriteAtomically(string destination, byte[] content)
    {
        var temporary = destination + ".tmp";
        File.WriteAllBytes(temporary, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temporary, destination, overwrite: true);
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

public static class CredentialProtectorFactory
{
    public static ICredentialProtector CreateDefault()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsDpapiCredentialProtector();
        }

        var key = Environment.GetEnvironmentVariable("AIHUB_ROUTER_MASTER_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            return new UnavailableCredentialProtector(
                "未设置 AIHUB_ROUTER_MASTER_KEY，凭据持久化已禁用");
        }

        try
        {
            return AesGcmCredentialProtector.FromBase64Key(key);
        }
        catch (FormatException)
        {
            return new UnavailableCredentialProtector(
                "AIHUB_ROUTER_MASTER_KEY 必须是 Base64 编码的 32 字节密钥");
        }
        catch (ArgumentException)
        {
            return new UnavailableCredentialProtector(
                "AIHUB_ROUTER_MASTER_KEY 必须是 Base64 编码的 32 字节密钥");
        }
    }
}

public sealed class AesGcmCredentialProtector : ICredentialProtector, IDisposable
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public AesGcmCredentialProtector(ReadOnlySpan<byte> key)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("AES-GCM 主密钥必须是 32 字节。", nameof(key));
        }

        _key = key.ToArray();
    }

    public bool IsAvailable => true;
    public string Description => "AES-256-GCM external master key";

    public static AesGcmCredentialProtector FromBase64Key(string base64Key)
    {
        var key = Convert.FromBase64String(base64Key.Trim());
        try
        {
            return new AesGcmCredentialProtector(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        var output = new byte[NonceSize + TagSize + plaintext.Length];
        var nonce = output.AsSpan(0, NonceSize);
        var tag = output.AsSpan(NonceSize, TagSize);
        var ciphertext = output.AsSpan(NonceSize + TagSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return output;
    }

    public byte[] Unprotect(ReadOnlySpan<byte> encrypted)
    {
        if (encrypted.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("认证配置格式无效。" );
        }

        var plaintext = new byte[encrypted.Length - NonceSize - TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(
            encrypted[..NonceSize],
            encrypted[(NonceSize + TagSize)..],
            encrypted.Slice(NonceSize, TagSize),
            plaintext);
        return plaintext;
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_key);
    }
}

public sealed class UnavailableCredentialProtector(string description) : ICredentialProtector
{
    public bool IsAvailable => false;
    public string Description { get; } = description;

    public byte[] Protect(ReadOnlySpan<byte> plaintext) =>
        throw new InvalidOperationException(Description);

    public byte[] Unprotect(ReadOnlySpan<byte> encrypted) =>
        throw new InvalidOperationException(Description);
}

internal sealed class WindowsDpapiCredentialProtector : ICredentialProtector
{
    private const int CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Entropy = "AIHubRouter/current-user/v1"u8.ToArray();

    public bool IsAvailable => OperatingSystem.IsWindows();
    public string Description => "Windows DPAPI current user";

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        var inputBytes = plaintext.ToArray();
        var input = CreateBlob(inputBytes);
        var entropy = CreateBlob(Entropy);
        var output = default(DataBlob);
        try
        {
            if (!CryptProtectData(
                    ref input,
                    "AIHubRouter credentials",
                    ref entropy,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 无法加密认证配置。" );
            }

            return CopyBlob(output);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(inputBytes);
            FreeAllocatedBlob(input, clear: true);
            FreeAllocatedBlob(entropy, clear: false);
            FreeLocalBlob(output, clear: false);
        }
    }

    public byte[] Unprotect(ReadOnlySpan<byte> encrypted)
    {
        var encryptedBytes = encrypted.ToArray();
        var input = CreateBlob(encryptedBytes);
        var entropy = CreateBlob(Entropy);
        var output = default(DataBlob);
        var description = IntPtr.Zero;
        try
        {
            if (!CryptUnprotectData(
                    ref input,
                    out description,
                    ref entropy,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 无法解密认证配置。" );
            }

            return CopyBlob(output);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptedBytes);
            FreeAllocatedBlob(input, clear: false);
            FreeAllocatedBlob(entropy, clear: false);
            FreeLocalBlob(output, clear: true);
            if (description != IntPtr.Zero)
            {
                LocalFree(description);
            }
        }
    }

    private static DataBlob CreateBlob(byte[] data)
    {
        var pointer = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, pointer, data.Length);
        return new DataBlob { Size = data.Length, Data = pointer };
    }

    private static byte[] CopyBlob(DataBlob blob)
    {
        if (blob.Size <= 0 || blob.Data == IntPtr.Zero)
        {
            return [];
        }

        var result = new byte[blob.Size];
        Marshal.Copy(blob.Data, result, 0, blob.Size);
        return result;
    }

    private static void FreeAllocatedBlob(DataBlob blob, bool clear)
    {
        if (blob.Data == IntPtr.Zero)
        {
            return;
        }

        if (clear)
        {
            ClearUnmanaged(blob);
        }

        Marshal.FreeHGlobal(blob.Data);
    }

    private static void FreeLocalBlob(DataBlob blob, bool clear)
    {
        if (blob.Data == IntPtr.Zero)
        {
            return;
        }

        if (clear)
        {
            ClearUnmanaged(blob);
        }

        LocalFree(blob.Data);
    }

    private static void ClearUnmanaged(DataBlob blob)
    {
        if (blob.Size <= 0)
        {
            return;
        }

        var zeroes = new byte[blob.Size];
        Marshal.Copy(zeroes, 0, blob.Data, zeroes.Length);
        CryptographicOperations.ZeroMemory(zeroes);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        out IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("Kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
