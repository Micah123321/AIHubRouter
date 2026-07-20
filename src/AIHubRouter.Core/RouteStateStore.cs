using System.Text.Json;

namespace AIHubRouter.Core;

public interface IRouteStateStore
{
    RouteState Load();
    void Save(RouteState state);
}

public sealed class JsonRouteStateStore(string storageDirectory) : IRouteStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly string _path = Path.Combine(storageDirectory, "route-state.json");

    public RouteState Load()
    {
        return File.Exists(_path)
            ? JsonSerializer.Deserialize<RouteState>(File.ReadAllText(_path), JsonOptions) ?? new RouteState()
            : new RouteState();
    }

    public void Save(RouteState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temporary, _path, overwrite: true);
    }
}

public sealed class ProfileLock : IDisposable
{
    private readonly FileStream _stream;

    private ProfileLock(FileStream stream)
    {
        _stream = stream;
    }

    public static ProfileLock? TryAcquire(string storageDirectory)
    {
        Directory.CreateDirectory(storageDirectory);
        var path = Path.Combine(storageDirectory, "router.lock");
        try
        {
            var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            return new ProfileLock(stream);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
    }
}
