using System.Threading.Channels;

namespace AIHubRouter.Cli;

internal sealed class ProfileFileChangeMonitor : IDisposable
{
    private static readonly HashSet<string> ProfileFiles = new(StringComparer.Ordinal)
    {
        "settings.json",
        "credentials.dat"
    };

    private readonly Channel<byte> _changes = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly FileSystemWatcher _watcher;

    public ProfileFileChangeMonitor(string storageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        Directory.CreateDirectory(storageDirectory);

        _watcher = new FileSystemWatcher(storageDirectory)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
    }

    public async ValueTask WaitForChangeAsync(CancellationToken cancellationToken) =>
        await _changes.Reader.ReadAsync(cancellationToken);

    public void Dispose()
    {
        _watcher.Changed -= OnChanged;
        _watcher.Created -= OnChanged;
        _watcher.Deleted -= OnChanged;
        _watcher.Renamed -= OnRenamed;
        _watcher.Error -= OnError;
        _watcher.Dispose();
        _changes.Writer.TryComplete();
    }

    private void OnChanged(object sender, FileSystemEventArgs eventArgs)
    {
        if (IsProfileFile(eventArgs.Name))
        {
            SignalChange();
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs eventArgs)
    {
        if (IsProfileFile(eventArgs.Name) || IsProfileFile(eventArgs.OldName))
        {
            SignalChange();
        }
    }

    private void OnError(object sender, ErrorEventArgs eventArgs) => SignalChange();

    private static bool IsProfileFile(string? name) =>
        name is not null && ProfileFiles.Contains(name);

    private void SignalChange() => _changes.Writer.TryWrite(0);
}
