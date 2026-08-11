using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHubRouter.Core;

/// <summary>
/// Runs one reliability probe with a detector binding's own credential.
/// </summary>
public interface IChannelReliabilityDetector
{
    Task<DetectorResult> DetectAsync(
        DetectorBinding? binding,
        string? model,
        string? apiKey,
        long? groupId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Controlled stdin/stdout adapter for scripts/channel_detector_worker.py.
/// </summary>
public sealed class ProcessChannelReliabilityDetector : IChannelReliabilityDetector
{
    public const int DefaultMaxStdoutBytes = 64 * 1024;
    public const int DefaultMaxStderrBytes = 32 * 1024;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    private const string WorkerModelPrefix = "gpt-5.6-";

    private readonly string _pythonCommand;
    private readonly string _workerPath;
    private readonly string _preset;
    private readonly string? _workingDirectory;
    private readonly TimeSpan _timeout;
    private readonly int _maxStdoutBytes;
    private readonly int _maxStderrBytes;

    public ProcessChannelReliabilityDetector(
        string pythonCommand,
        string workerPath,
        string preset = "low",
        TimeSpan? timeout = null,
        int maxStdoutBytes = DefaultMaxStdoutBytes,
        int maxStderrBytes = DefaultMaxStderrBytes,
        string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pythonCommand);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(preset);
        if (timeout is not null && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (maxStdoutBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxStdoutBytes));
        }

        if (maxStderrBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxStderrBytes));
        }

        _pythonCommand = pythonCommand.Trim();
        _workerPath = workerPath.Trim();
        _preset = preset.Trim();
        _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? null
            : Path.GetFullPath(workingDirectory);
        _timeout = timeout ?? DefaultTimeout;
        _maxStdoutBytes = maxStdoutBytes;
        _maxStderrBytes = maxStderrBytes;
    }

    public async Task<DetectorResult> DetectAsync(
        DetectorBinding? binding,
        string? model,
        string? apiKey,
        long? groupId = null,
        CancellationToken cancellationToken = default)
    {
        var keyId = binding?.KeyId ?? 0;
        var normalizedModel = NormalizeModel(model, out var workerModel);
        var checkedAt = DateTimeOffset.UtcNow;

        if (cancellationToken.IsCancellationRequested)
        {
            return Failure(keyId, groupId, normalizedModel, DetectorErrorCategory.Cancelled,
                ChannelReliabilityStatus.Unavailable, checkedAt);
        }

        if (binding is null || !binding.Enabled || string.IsNullOrWhiteSpace(binding.BaseUrl))
        {
            return Failure(keyId, groupId, normalizedModel, DetectorErrorCategory.MissingBinding,
                ChannelReliabilityStatus.Unconfigured, checkedAt);
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Failure(keyId, groupId, normalizedModel, DetectorErrorCategory.MissingCredential,
                ChannelReliabilityStatus.Unconfigured, checkedAt);
        }

        if (workerModel is null)
        {
            return Failure(keyId, groupId, string.Empty, DetectorErrorCategory.InvalidResponse,
                ChannelReliabilityStatus.EvidenceInsufficient, checkedAt);
        }

        if (!WorkerExists())
        {
            return Failure(keyId, groupId, normalizedModel, DetectorErrorCategory.WorkerUnavailable,
                ChannelReliabilityStatus.Unavailable, checkedAt);
        }

        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _pythonCommand,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (_workingDirectory is not null)
            {
                startInfo.WorkingDirectory = _workingDirectory;
            }

            // The worker path is non-secret; the credential is sent below via stdin.
            startInfo.ArgumentList.Add(_workerPath);
            process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                process.Dispose();
                return Failure(keyId, groupId, normalizedModel,
                    DetectorErrorCategory.WorkerStartFailed,
                    ChannelReliabilityStatus.Unavailable, checkedAt);
            }
        }
        catch (Win32Exception exception) when (IsMissingExecutable(exception))
        {
            process?.Dispose();
            return Failure(keyId, groupId, normalizedModel,
                DetectorErrorCategory.WorkerUnavailable,
                ChannelReliabilityStatus.Unavailable, checkedAt);
        }
        catch (Exception)
        {
            process?.Dispose();
            return Failure(keyId, groupId, normalizedModel,
                DetectorErrorCategory.WorkerStartFailed,
                ChannelReliabilityStatus.Unavailable, checkedAt);
        }

        using (process)
        using (var timeoutCancellation = new CancellationTokenSource(_timeout))
        using (var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                   cancellationToken, timeoutCancellation.Token))
        {
            var stdoutTask = ChannelReliabilityProcessIo.ReadBoundedAsync(
                process.StandardOutput.BaseStream, _maxStdoutBytes, linkedCancellation.Token);
            var stderrTask = ChannelReliabilityProcessIo.DrainBoundedAsync(
                process.StandardError.BaseStream, _maxStderrBytes, linkedCancellation.Token);
            var timedOut = false;
            var cancelled = false;
            var executionFailed = false;

            try
            {
                await WriteRequestAsync(
                    process, binding.BaseUrl, workerModel, apiKey, _preset,
                    linkedCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                timedOut = !cancellationToken.IsCancellationRequested &&
                    timeoutCancellation.IsCancellationRequested;
                cancelled = cancellationToken.IsCancellationRequested;
                ChannelReliabilityProcessIo.TryKill(process);
            }
            catch (Exception)
            {
                // A closed stdin is handled as a safe worker failure; stderr is never exposed.
                executionFailed = true;
            }

            if (!timedOut && !cancelled && !ChannelReliabilityProcessIo.HasExited(process))
            {
                try
                {
                    await process.WaitForExitAsync(linkedCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    timedOut = !cancellationToken.IsCancellationRequested &&
                        timeoutCancellation.IsCancellationRequested;
                    cancelled = cancellationToken.IsCancellationRequested;
                    ChannelReliabilityProcessIo.TryKill(process);
                }
                catch (Exception)
                {
                    executionFailed = true;
                    ChannelReliabilityProcessIo.TryKill(process);
                }
            }

            if (timedOut || cancelled)
            {
                ChannelReliabilityProcessIo.TryKill(process);
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);
            return ChannelReliabilityResultMapper.MapProcessResult(
                keyId, groupId, normalizedModel, checkedAt, stdout,
                ChannelReliabilityProcessIo.GetExitCode(process), executionFailed, timedOut, cancelled);
        }
    }

    private static async Task WriteRequestAsync(
        Process process,
        string baseUrl,
        string model,
        string apiKey,
        string preset,
        CancellationToken cancellationToken)
    {
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(
            new WorkerRequest(baseUrl, model, apiKey, preset));
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(requestBytes, cancellationToken)
                .ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(requestBytes);
            process.StandardInput.Close();
        }
    }

    private static string NormalizeModel(string? model, out string? workerModel)
    {
        workerModel = null;
        if (string.IsNullOrWhiteSpace(model))
        {
            return string.Empty;
        }

        var trimmed = model.Trim();
        var shortName = trimmed.StartsWith(WorkerModelPrefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[WorkerModelPrefix.Length..]
            : trimmed;
        var normalized = DetectorModelNames.Normalize(shortName);
        if (normalized is null)
        {
            return string.Empty;
        }

        workerModel = WorkerModelPrefix + normalized;
        return normalized;
    }

    private static DetectorResult Failure(
        long keyId,
        long? groupId,
        string model,
        DetectorErrorCategory category,
        ChannelReliabilityStatus status,
        DateTimeOffset checkedAt,
        bool? official = null,
        string? claimedModel = null,
        string? title = null,
        DetectorNetworkSummary? networkSummary = null,
        DetectorEvidenceSummary? evidenceSummary = null) => new()
        {
            KeyId = keyId,
            GroupId = groupId,
            Model = model,
            Status = status,
            Verdict = DetectorVerdict.EvidenceInsufficient,
            ErrorCategory = category,
            CheckedAt = checkedAt,
            Official = official,
            ClaimedModel = claimedModel,
            Title = title,
            NetworkSummary = networkSummary,
            EvidenceSummary = evidenceSummary
        };

    private static bool IsMissingExecutable(Win32Exception exception) =>
        exception.NativeErrorCode is 2 or 3 or 126 or 127;

    private bool WorkerExists()
    {
        try
        {
            var path = Path.IsPathRooted(_workerPath)
                ? _workerPath
                : Path.Combine(_workingDirectory ?? Directory.GetCurrentDirectory(), _workerPath);
            return File.Exists(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed record WorkerRequest(
        [property: JsonPropertyName("base_url")] string BaseUrl,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("api_key")] string ApiKey,
        [property: JsonPropertyName("preset")] string Preset);

}
