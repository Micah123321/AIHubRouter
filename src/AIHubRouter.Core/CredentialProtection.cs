using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace AIHubRouter.Core;

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
