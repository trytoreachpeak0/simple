using System.Diagnostics;
using System.Text;

namespace Simple.Cli.Remote;

/// <summary>Runs a process and hands each line it prints to a callback as it comes; cancelling kills it with its children.</summary>
public static class LineProcess
{
    /// <summary>The line written into the process's input every <c>heartbeat</c>.</summary>
    public const string HeartbeatLine = "alive";

    /// <param name="heartbeat">
    /// When given, the process's input is a pipe that gets <see cref="HeartbeatLine"/>
    /// at this interval until the process ends, and is then closed: for ssh,
    /// how the remote side tells this end is still there.
    /// </param>
    public static async Task<int> RunAsync(
        ProcessStartInfo start, Action<string> output, Action<string> error, CancellationToken cancellationToken, TimeSpan? heartbeat = null)
    {
        ArgumentNullException.ThrowIfNull(start);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.RedirectStandardInput = heartbeat is not null;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        start.UseShellExecute = false;
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                output(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                error(e.Data);
            }
        };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new RemoteException(ExitCodes.RemoteFailed, $"Could not start {start.FileName}: {e.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var stop = new CancellationTokenSource();
        var beating = heartbeat is { } interval ? BeatAsync(process.StandardInput, interval, stop.Token) : Task.CompletedTask;
        try
        {
            // Also waits until both streams are read to the end.
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        finally
        {
            await stop.CancelAsync();
            await beating;
            if (heartbeat is not null)
            {
                try
                {
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                    // The process has ended.
                }
            }
        }

        return process.ExitCode;
    }

    private static async Task BeatAsync(StreamWriter input, TimeSpan interval, CancellationToken stop)
    {
        try
        {
            while (true)
            {
                await input.WriteLineAsync(HeartbeatLine.AsMemory(), stop);
                await input.FlushAsync(stop);
                await Task.Delay(interval, stop);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Stopped, or the process has ended.
        }
    }
}
