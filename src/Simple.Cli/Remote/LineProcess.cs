using System.Diagnostics;
using System.Text;

namespace Simple.Cli.Remote;

/// <summary>Runs a process and hands each line it prints to a callback as it comes; cancelling kills it with its children.</summary>
public static class LineProcess
{
    public static async Task<int> RunAsync(ProcessStartInfo start, Action<string> output, Action<string> error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
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

        return process.ExitCode;
    }
}
