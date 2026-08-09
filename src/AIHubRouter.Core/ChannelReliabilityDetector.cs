using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
    private const int ReadBufferSize = 4096;
    private const int MaxScalarLength = 4096;

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
            var stdoutTask = ReadBoundedAsync(
                process.StandardOutput.BaseStream, _maxStdoutBytes, linkedCancellation.Token);
            var stderrTask = DrainBoundedAsync(
                process.StandardError.BaseStream, _maxStderrBytes, linkedCancellation.Token);
            var timedOut = false;
            var cancelled = false;
            var writeFailed = false;

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
                TryKill(process);
            }
            catch (Exception)
            {
                // A closed stdin is handled as a safe worker failure; stderr is never exposed.
                writeFailed = true;
            }

            if (!timedOut && !cancelled && !HasExited(process))
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
                    TryKill(process);
                }
                catch (Exception)
                {
                    TryKill(process);
                }
            }

            if (timedOut || cancelled)
            {
                TryKill(process);
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);
            return MapProcessResult(
                keyId, groupId, normalizedModel, checkedAt, stdout,
                GetExitCode(process), writeFailed, timedOut, cancelled);
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

    private static DetectorResult MapProcessResult(
        long keyId,
        long? groupId,
        string model,
        DateTimeOffset checkedAt,
        BoundedOutput stdout,
        int exitCode,
        bool writeFailed,
        bool timedOut,
        bool cancelled)
    {
        if (cancelled)
        {
            return Failure(keyId, groupId, model, DetectorErrorCategory.Cancelled,
                ChannelReliabilityStatus.Unavailable, checkedAt);
        }

        if (timedOut)
        {
            return Failure(keyId, groupId, model, DetectorErrorCategory.Timeout,
                ChannelReliabilityStatus.Unavailable, checkedAt);
        }

        if (stdout.Truncated)
        {
            return Failure(keyId, groupId, model, DetectorErrorCategory.StreamTruncated,
                ChannelReliabilityStatus.Unavailable, checkedAt);
        }

        if (!TryReadLastResponse(stdout.Text, out var response))
        {
            var category = writeFailed || exitCode != 0
                ? DetectorErrorCategory.Unknown
                : DetectorErrorCategory.InvalidResponse;
            return Failure(keyId, groupId, model, category,
                ChannelReliabilityStatus.EvidenceInsufficient, checkedAt);
        }

        if (response.ClaimedModel is not null &&
            !string.Equals(response.ClaimedModel, WorkerModelPrefix + model,
                StringComparison.OrdinalIgnoreCase))
        {
            return Failure(keyId, groupId, model, DetectorErrorCategory.InvalidResponse,
                ChannelReliabilityStatus.EvidenceInsufficient, checkedAt);
        }

        var status = response.Status.Trim().ToLowerInvariant();
        if (status == "complete")
        {
            var verdict = ParseVerdict(response.OverallVerdict);
            if (verdict is null || exitCode != 0)
            {
                return Failure(keyId, groupId, model, DetectorErrorCategory.InvalidResponse,
                    ChannelReliabilityStatus.EvidenceInsufficient, checkedAt);
            }

            return new DetectorResult
            {
                KeyId = keyId,
                GroupId = groupId,
                Model = model,
                Status = verdict == DetectorVerdict.Passed
                    ? ChannelReliabilityStatus.Passed
                    : ChannelReliabilityStatus.EvidenceInsufficient,
                Verdict = verdict.Value,
                ErrorCategory = DetectorErrorCategory.None,
                CheckedAt = checkedAt,
                Official = response.Official,
                Title = VerdictTitle(verdict.Value)
            };
        }

        if (status == "evidence_insufficient")
        {
            return Failure(keyId, groupId, model, DetectorErrorCategory.EvidenceInsufficient,
                ChannelReliabilityStatus.EvidenceInsufficient, checkedAt, response.Official);
        }

        if (status == "error")
        {
            var category = ParseErrorCategory(response.ErrorCode);
            return Failure(keyId, groupId, model, category,
                category == DetectorErrorCategory.EvidenceInsufficient
                    ? ChannelReliabilityStatus.EvidenceInsufficient
                    : ChannelReliabilityStatus.Unavailable,
                checkedAt, response.Official);
        }

        return Failure(keyId, groupId, model, DetectorErrorCategory.InvalidResponse,
            ChannelReliabilityStatus.EvidenceInsufficient, checkedAt);
    }

    private static bool TryReadLastResponse(string stdout, out WorkerResponse response)
    {
        response = default!;
        foreach (var line in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            try
            {
                using var document = JsonDocument.Parse(line, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8
                });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !TryString(root, "status", out var status))
                {
                    continue;
                }

                response = new WorkerResponse(
                    status,
                    TryString(root, "overall_verdict", out var verdict) ? verdict : null,
                    TryString(root, "error_code", out var errorCode) ? errorCode : null,
                    TryBoolean(root, "official", out var official) ? official : null,
                    TryString(root, "claimed_model", out var claimedModel) ? claimedModel : null);
                return true;
            }
            catch (JsonException)
            {
                // Worker logs or malformed lines do not become application output.
            }
        }

        return false;
    }

    private static bool TryString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var candidate = property.GetString();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > MaxScalarLength)
        {
            return false;
        }

        value = candidate;
        return true;
    }

    private static bool TryBoolean(JsonElement root, string name, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(name, out var property) ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
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

    private static DetectorVerdict? ParseVerdict(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "通过" or "passed" => DetectorVerdict.Passed,
        "可能非gpt" or "possible_non_gpt" => DetectorVerdict.PossibleNonGpt,
        "juice混用" or "juice_mixed" => DetectorVerdict.JuiceMixed,
        "仅概率探针混用" or "probability_only_mixed" => DetectorVerdict.ProbabilityOnlyMixed,
        "juice通过但概率探针证据不足" or "evidence_insufficient" =>
            DetectorVerdict.EvidenceInsufficient,
        _ => null
    };

    private static DetectorErrorCategory ParseErrorCategory(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "timeout" => DetectorErrorCategory.Timeout,
            "http_error" => DetectorErrorCategory.HttpError,
            "truncated_stream" => DetectorErrorCategory.StreamTruncated,
            "evidence_insufficient" => DetectorErrorCategory.EvidenceInsufficient,
            "invalid_input" => DetectorErrorCategory.InvalidResponse,
            _ => DetectorErrorCategory.Unknown
        };

    private static string VerdictTitle(DetectorVerdict verdict) => verdict switch
    {
        DetectorVerdict.Passed => "通过",
        DetectorVerdict.PossibleNonGpt => "可能非GPT",
        DetectorVerdict.JuiceMixed => "Juice混用",
        DetectorVerdict.ProbabilityOnlyMixed => "仅概率探针混用",
        _ => "未形成正式结论"
    };

    private static DetectorResult Failure(
        long keyId,
        long? groupId,
        string model,
        DetectorErrorCategory category,
        ChannelReliabilityStatus status,
        DateTimeOffset checkedAt,
        bool? official = null) => new()
        {
            KeyId = keyId,
            GroupId = groupId,
            Model = model,
            Status = status,
            Verdict = DetectorVerdict.EvidenceInsufficient,
            ErrorCategory = category,
            CheckedAt = checkedAt,
            Official = official
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

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static int GetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (NotSupportedException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    private static async Task<BoundedOutput> ReadBoundedAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maxBytes, ReadBufferSize));
        var buffer = new byte[ReadBufferSize];
        var stored = 0;
        var truncated = false;
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var count = Math.Min(maxBytes - stored, read);
                if (count > 0)
                {
                    output.Write(buffer, 0, count);
                    stored += count;
                }

                truncated |= count != read;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            truncated = true;
        }

        return new BoundedOutput(Encoding.UTF8.GetString(output.ToArray()), truncated);
    }

    private static async Task<bool> DrainBoundedAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[ReadBufferSize];
        var stored = 0;
        var truncated = false;
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var count = Math.Min(maxBytes - stored, read);
                stored += count;
                truncated |= count != read;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            truncated = true;
        }

        return truncated;
    }

    private sealed record WorkerRequest(
        [property: JsonPropertyName("base_url")] string BaseUrl,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("api_key")] string ApiKey,
        [property: JsonPropertyName("preset")] string Preset);

    private sealed record WorkerResponse(
        string Status,
        string? OverallVerdict,
        string? ErrorCode,
        bool? Official,
        string? ClaimedModel);

    private sealed record BoundedOutput(string Text, bool Truncated);
}
