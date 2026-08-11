using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHubRouter.Core;

/// <summary>
/// Persists local channel quarantine decisions without coupling them to the
/// main settings or credentials files.
/// </summary>
public interface IChannelQuarantineStore
{
    /// <summary>
    /// Returns every persisted decision in insertion order.
    /// </summary>
    IReadOnlyList<ChannelQuarantineRecord> LoadHistory();

    /// <summary>
    /// Returns the most recent decision for each group, including expired
    /// decisions so callers can display their last known state.
    /// </summary>
    IReadOnlyList<ChannelQuarantineRecord> LoadLatest();

    /// <summary>
    /// Returns the most recent decisions that are still active at the supplied
    /// instant. The comparison is made using the UTC instant, not wall-clock
    /// representations.
    /// </summary>
    IReadOnlyList<ChannelQuarantineRecord> GetActive(DateTimeOffset utcNow);

    /// <summary>
    /// Appends a decision to history and replaces the latest decision for its
    /// group. Persistence errors are intentionally allowed to propagate.
    /// </summary>
    void Save(ChannelQuarantineRecord record);
}

/// <summary>
/// JSON-backed quarantine store for one profile directory.
/// </summary>
public sealed class JsonChannelQuarantineStore : IChannelQuarantineStore
{
    public const string FileName = "channel-reliability.json";
    public const int MaxHistoryEntries = 512;
    public static readonly TimeSpan DefaultIsolationDuration = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly string _storageDirectory;
    private readonly string _path;

    public JsonChannelQuarantineStore(string storageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);

        _storageDirectory = Path.GetFullPath(storageDirectory);
        _path = Path.Combine(_storageDirectory, FileName);
    }

    public string StoragePath => _path;

    public IReadOnlyList<ChannelQuarantineRecord> LoadHistory()
    {
        lock (_gate)
        {
            return ReadDocument().History.ToArray();
        }
    }

    public IReadOnlyList<ChannelQuarantineRecord> LoadLatest()
    {
        lock (_gate)
        {
            return ReadDocument().Latest.ToArray();
        }
    }

    public IReadOnlyList<ChannelQuarantineRecord> GetActive(DateTimeOffset utcNow)
    {
        var instant = utcNow.ToUniversalTime();

        lock (_gate)
        {
            return ReadDocument()
                .Latest
                .Where(record => record.ExpiresAt > instant)
                .ToArray();
        }
    }

    public void Save(ChannelQuarantineRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_gate)
        {
            var document = ReadDocument();
            document.History.Add(record);
            TrimHistory(document);

            var latestIndex = document.Latest.FindIndex(
                current => current.GroupId == record.GroupId);
            if (latestIndex >= 0)
            {
                document.Latest[latestIndex] = record;
            }
            else
            {
                document.Latest.Add(record);
            }

            WriteDocument(document);
        }
    }

    private ChannelQuarantineDocument ReadDocument()
    {
        if (!File.Exists(_path))
        {
            return new ChannelQuarantineDocument();
        }

        var json = File.ReadAllText(_path, Encoding.UTF8);
        var document = JsonSerializer.Deserialize<ChannelQuarantineDocument>(json, JsonOptions);
        if (document is null)
        {
            throw new JsonException($"隔离状态文件为空：{_path}");
        }

        document.History ??= [];
        document.Latest ??= [];
        TrimHistory(document);
        if (document.Latest.Count == 0 && document.History.Count > 0)
        {
            foreach (var record in document.History)
            {
                var latestIndex = document.Latest.FindIndex(
                    current => current.GroupId == record.GroupId);
                if (latestIndex >= 0)
                {
                    document.Latest[latestIndex] = record;
                }
                else
                {
                    document.Latest.Add(record);
                }
            }
        }

        return document;
    }

    private static void TrimHistory(ChannelQuarantineDocument document)
    {
        if (document.History.Count > MaxHistoryEntries)
        {
            document.History = document.History
                .TakeLast(MaxHistoryEntries)
                .ToList();
        }
    }

    private void WriteDocument(ChannelQuarantineDocument document)
    {
        Directory.CreateDirectory(_storageDirectory);
        SetPrivateDirectoryMode(_storageDirectory);

        var temporary = Path.Combine(
            _storageDirectory,
            $".{FileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.SequentialScan))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            SetPrivateFileMode(temporary);

            // The temporary file is in the same directory, so the rename is
            // an atomic replacement on the supported file systems.
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            TryDeleteTemporary(temporary);
        }
    }

    private static void SetPrivateDirectoryMode(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(
            directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void SetPrivateFileMode(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void TryDeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Preserve the original write/replace exception. The temporary
            // name is private to this operation and is safe to retry later.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup must not hide the persistence error that caused it.
        }
    }

    private sealed class ChannelQuarantineDocument
    {
        [JsonPropertyName("history")]
        public List<ChannelQuarantineRecord> History { get; set; } = [];

        [JsonPropertyName("latest")]
        public List<ChannelQuarantineRecord> Latest { get; set; } = [];
    }
}
