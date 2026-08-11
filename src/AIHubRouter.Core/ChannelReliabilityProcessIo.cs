using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AIHubRouter.Core;

internal static class ChannelReliabilityProcessIo
{
    private const int ReadBufferSize = 4096;

    public static bool HasExited(Process process)
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

    public static int GetExitCode(Process process)
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

    public static void TryKill(Process process)
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

    public static async Task<BoundedOutput> ReadBoundedAsync(
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

    public static async Task<bool> DrainBoundedAsync(
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
}

internal sealed record BoundedOutput(string Text, bool Truncated);
