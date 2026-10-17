using System.Diagnostics;
using System.Text;

namespace Simple.Cli.Remote;

/// <summary>How <see cref="LineProcess"/> runs a process beyond reading its output.</summary>
/// <param name="Heartbeat">
/// When given, the process's input is a pipe that gets <see cref="LineProcess.HeartbeatLine"/>
/// at this interval until the process ends, and is then closed: for ssh, how
/// the remote side tells this end is still there.
/// </param>
/// <param name="Beats">How many heartbeats to write before going silent while staying connected; null, until the end. Stands for a lost network in tests.</param>
/// <param name="KillOnCancel">
/// Cancelling kills the process with its children (true), or only closes its
/// input and stops reading it, leaving it running (false): what sshd does to a
/// session whose client is gone. The test machine plays sshd with false.
/// </param>
/// <param name="Started">Told about the process once it has started.</param>
public sealed record LineProcessOptions(
    TimeSpan? Heartbeat = null,
    int? Beats = null,
    bool KillOnCancel = true,
    Action<Process>? Started = null);

/// <summary>Runs a process and hands each line it prints to a callback as it comes.</summary>
public static class LineProcess
{
    /// <summary>The line written into the process's input every heartbeat.</summary>
    public const string HeartbeatLine = "alive";

    public static async Task<int> RunAsync(
        ProcessStartInfo start, Action<string> output, Action<string> error, CancellationToken cancellationToken, LineProcessOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(start);
        options ??= new LineProcessOptions();
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.RedirectStandardInput = options.Heartbeat is not null;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        start.UseShellExecute = false;
        var process = new Process { StartInfo = start };
        using var stop = new CancellationTokenSource();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null && !stop.IsCancellationRequested)
            {
                output(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null && !stop.IsCancellationRequested)
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
            process.Dispose();
            throw new RemoteException(ExitCodes.RemoteFailed, $"Could not start {start.FileName}: {e.Message}");
        }

        options.Started?.Invoke(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var beating = options.Heartbeat is { } interval ? BeatAsync(process.StandardInput, interval, options.Beats, stop.Token) : Task.CompletedTask;
        try
        {
            // Also waits until both streams are read to the end.
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await stop.CancelAsync();
            if (options.KillOnCancel)
            {
                process.Kill(entireProcessTree: true);
            }

            CloseInput(process, options);
            throw;
        }
        finally
        {
            await stop.CancelAsync();
            await beating;
        }

        CloseInput(process, options);
        var exitCode = process.ExitCode;
        process.Dispose();
        return exitCode;
    }

    private static void CloseInput(Process process, LineProcessOptions options)
    {
        if (options.Heartbeat is null)
        {
            return;
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The process has ended.
        }
    }

    private static async Task BeatAsync(StreamWriter input, TimeSpan interval, int? beats, CancellationToken stop)
    {
        try
        {
            for (var written = 0; ; written++)
            {
                if (beats is not { } limit || written < limit)
                {
                    await input.WriteLineAsync(HeartbeatLine.AsMemory(), stop);
                    await input.FlushAsync(stop);
                }

                await Task.Delay(interval, stop);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Stopped, or the process has ended.
        }
    }
}
