using System.Security.Cryptography;
using System.Text.Json;

namespace AIHubRouter.Core;

public sealed partial class AppSettingsStore
{
    private void CommitFiles(byte[] settingsContent, byte[]? encryptedCredentials, bool removeCredentials)
    {
        EnsureStorageDirectory();

        var settingsTemporary = GetTemporaryPath(_settingsPath);
        var credentialsTemporary = encryptedCredentials is null
            ? null
            : GetTemporaryPath(_credentialsPath);
        var settingsBackup = GetTemporaryPath(_settingsPath + ".backup");
        var credentialsBackup = GetTemporaryPath(_credentialsPath + ".backup");
        var credentialsChanged = encryptedCredentials is not null || removeCredentials;
        var transaction = new PersistenceTransaction(
            settingsTemporary,
            credentialsTemporary,
            settingsBackup,
            credentialsBackup,
            credentialsChanged,
            encryptedCredentials is not null,
            File.Exists(_settingsPath),
            credentialsChanged && File.Exists(_credentialsPath),
            false,
            false,
            false,
            false,
            false);

        try
        {
            WriteTemporary(settingsTemporary, settingsContent);
            if (encryptedCredentials is not null && credentialsTemporary is not null)
            {
                WriteTemporary(credentialsTemporary, encryptedCredentials);
            }
            WriteTransaction(transaction);

            if (transaction.SettingsOriginallyExists)
            {
                File.Move(_settingsPath, settingsBackup);
                transaction = transaction with { SettingsBackedUp = true };
                WriteTransaction(transaction);
            }

            if (transaction.CredentialsOriginallyExists)
            {
                File.Move(_credentialsPath, credentialsBackup);
                transaction = transaction with { CredentialsBackedUp = true };
                WriteTransaction(transaction);
            }

            File.Move(settingsTemporary, _settingsPath);
            transaction = transaction with { SettingsCommitted = true };
            WriteTransaction(transaction);

            if (encryptedCredentials is not null && credentialsTemporary is not null)
            {
                File.Move(credentialsTemporary, _credentialsPath);
                transaction = transaction with { CredentialsCommitted = true };
                WriteTransaction(transaction);
            }
            else if (credentialsChanged)
            {
                transaction = transaction with { CredentialsCommitted = true };
                WriteTransaction(transaction);
            }

            transaction = transaction with { CommitCompleted = true };
            WriteTransaction(transaction);
        }
        catch (Exception error)
        {
            try
            {
                RollbackTransaction(transaction);
                CleanupTransactionFiles(transaction);
                DeleteIfExists(_transactionPath);
            }
            catch (Exception recoveryError)
            {
                throw new IOException(
                    "持久化提交失败，且无法恢复原配置。请检查配置目录权限后重试。",
                    new AggregateException(error, recoveryError));
            }

            throw;
        }

        try
        {
            CleanupTransactionFiles(transaction);
            DeleteIfExists(_transactionPath);
        }
        catch (Exception cleanupError)
        {
            throw new IOException(
                "配置已提交，但旧事务文件清理失败；下次加载时会自动重试清理。",
                cleanupError);
        }
    }

    private void RecoverPendingTransaction()
    {
        if (!File.Exists(_transactionPath))
        {
            return;
        }

        PersistenceTransaction transaction;
        try
        {
            transaction = JsonSerializer.Deserialize<PersistenceTransaction>(
                File.ReadAllText(_transactionPath),
                JsonOptions) ?? throw new InvalidDataException("持久化事务记录为空。");
            ValidateTransaction(transaction);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException)
        {
            throw new InvalidOperationException(
                "持久化事务记录损坏，无法安全恢复配置。请检查配置目录后重试。",
                error);
        }

        try
        {
            if (transaction.CommitCompleted)
            {
                CleanupTransactionFiles(transaction);
            }
            else
            {
                RollbackTransaction(transaction);
                CleanupTransactionFiles(transaction);
            }

            DeleteIfExists(_transactionPath);
        }
        catch (Exception error)
        {
            throw new IOException(
                "上一次配置保存未完成，且自动恢复失败。请检查配置目录权限后重试。",
                error);
        }
    }

    private void ValidateTransaction(PersistenceTransaction transaction)
    {
        ValidateTransactionPath(
            transaction.SettingsTemporary,
            Path.GetFileName(_settingsPath));
        ValidateTransactionPath(
            transaction.SettingsBackup,
            Path.GetFileName(_settingsPath) + ".backup");
        if (transaction.CredentialsTemporary is not null)
        {
            ValidateTransactionPath(
                transaction.CredentialsTemporary,
                Path.GetFileName(_credentialsPath));
        }

        ValidateTransactionPath(
            transaction.CredentialsBackup,
            Path.GetFileName(_credentialsPath) + ".backup");
    }

    private void ValidateTransactionPath(string? path, string expectedPrefix)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException("持久化事务记录路径为空。");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("持久化事务记录路径无效。", exception);
        }

        var storageDirectory = Path.GetFullPath(_storageDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var pathDirectory = Path.GetDirectoryName(fullPath)?.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var fileName = Path.GetFileName(fullPath);
        var expectedFilePrefix = Path.GetFileName(expectedPrefix);
        var randomPartStart = expectedFilePrefix.Length + 1;
        var randomPartLength = fileName.Length - randomPartStart - ".tmp".Length;
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(storageDirectory, pathDirectory, pathComparison) ||
            !fileName.StartsWith(expectedFilePrefix + ".", pathComparison) ||
            !fileName.EndsWith(".tmp", pathComparison) ||
            randomPartLength != 32 ||
            !Guid.TryParseExact(fileName.Substring(randomPartStart, randomPartLength), "N", out _))
        {
            throw new InvalidDataException("持久化事务记录路径无效。");
        }
    }

    private void WriteTransaction(PersistenceTransaction transaction)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(transaction, JsonOptions);
        var temporary = GetTemporaryPath(_transactionPath);
        try
        {
            WriteTemporary(temporary, content);
            File.Move(temporary, _transactionPath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
            DeleteIfExists(temporary);
        }
    }

    private void RollbackTransaction(PersistenceTransaction transaction)
    {
        var settingsCommitted = transaction.SettingsCommitted ||
            (!File.Exists(transaction.SettingsTemporary) && File.Exists(_settingsPath));
        RestoreFile(
            _settingsPath,
            transaction.SettingsBackup,
            transaction.SettingsBackedUp || File.Exists(transaction.SettingsBackup),
            settingsCommitted);

        if (transaction.CredentialsChanged)
        {
            var credentialsCommitted = transaction.CredentialsCommitted ||
                transaction.CredentialsExpected &&
                !File.Exists(transaction.CredentialsTemporary) &&
                File.Exists(_credentialsPath);
            RestoreFile(
                _credentialsPath,
                transaction.CredentialsBackup,
                transaction.CredentialsBackedUp || File.Exists(transaction.CredentialsBackup),
                credentialsCommitted);
        }
    }

    private static void CleanupTransactionFiles(PersistenceTransaction transaction)
    {
        DeleteIfExists(transaction.SettingsTemporary);
        if (transaction.CredentialsTemporary is not null)
        {
            DeleteIfExists(transaction.CredentialsTemporary);
        }

        DeleteIfExists(transaction.SettingsBackup);
        DeleteIfExists(transaction.CredentialsBackup);
    }

    private sealed record PersistenceTransaction(
        string SettingsTemporary,
        string? CredentialsTemporary,
        string SettingsBackup,
        string CredentialsBackup,
        bool CredentialsChanged,
        bool CredentialsExpected,
        bool SettingsOriginallyExists,
        bool CredentialsOriginallyExists,
        bool CredentialsCommitted,
        bool SettingsBackedUp,
        bool CredentialsBackedUp,
        bool SettingsCommitted,
        bool CommitCompleted);

    private static void RestoreFile(
        string destination,
        string backup,
        bool backedUp,
        bool committed)
    {
        if (committed)
        {
            DeleteIfExists(destination);
        }

        if (backedUp && File.Exists(backup))
        {
            File.Move(backup, destination, overwrite: true);
        }
    }

    private static void WriteTemporary(string path, byte[] content)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            options: FileOptions.WriteThrough);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static string GetTemporaryPath(string destination) =>
        $"{destination}.{Guid.NewGuid():N}.tmp";

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
