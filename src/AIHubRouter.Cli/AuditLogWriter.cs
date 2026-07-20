using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;

namespace AIHubRouter.Cli;

internal sealed class AuditLogWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _path;
    private readonly long _maximumBytes;
    private readonly int _retainedFiles;

    public AuditLogWriter(string path, int maximumMegabytes, int retainedFiles)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("日志路径不能为空。", nameof(path));
        }

        _path = Path.GetFullPath(path);
        _maximumBytes = Math.Clamp(maximumMegabytes, 1, 1024) * 1024L * 1024L;
        _retainedFiles = Math.Clamp(retainedFiles, 1, 30);
        EnsureDirectory();
    }

    public void Write(object entry)
    {
        var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
        RotateIfNeeded(Encoding.UTF8.GetByteCount(line));

        var streamOptions = new FileStreamOptions
        {
            Mode = FileMode.Append,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            Options = FileOptions.SequentialScan
        };
        if (!OperatingSystem.IsWindows())
        {
            streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(_path, streamOptions))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(line);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private void EnsureDirectory()
    {
        var directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var created = !Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        if (created && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private void RotateIfNeeded(int incomingBytes)
    {
        var current = new FileInfo(_path);
        if (!current.Exists || current.Length + incomingBytes <= _maximumBytes)
        {
            return;
        }

        for (var index = _retainedFiles; index >= 2; index--)
        {
            var source = $"{_path}.{index - 1}";
            var destination = $"{_path}.{index}";
            if (File.Exists(source))
            {
                File.Move(source, destination, overwrite: true);
            }
        }

        File.Move(_path, $"{_path}.1", overwrite: true);
    }
}
