using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace Simple.Cli.Processes;

/// <summary>
/// Starts a component as a process of its own that outlives the command line,
/// with its output going straight into its log file, and stops it the way a
/// person pressing Ctrl+Break in its console would, so it shuts down
/// gracefully.
/// </summary>
/// <remarks>
/// <see cref="Process.Start(ProcessStartInfo)"/> cannot do either: it can only
/// redirect output into pipes that the command line would have to keep
/// draining, and it lets the child inherit every inheritable handle,
/// including the pipe a caller (CI, a test) reads the command line's output
/// from, so that caller would wait until the component exits. Here the child
/// inherits its log file and nothing else.
/// </remarks>
public static class ChildProcess
{
    private const uint ProcThreadAttributeHandleList = 0x00020002;
    private const int AccessDenied = 5;

    /// <summary>
    /// Starts <paramref name="executable"/> in a hidden console of its own.
    /// Returns its process id and start time, read from the process handle
    /// while it is still open, so even a process that exits at once is
    /// recorded as itself.
    /// </summary>
    /// <param name="breakAway">
    /// Start it outside the job the command line runs in. Only simple remote
    /// up asks for it: over SSH the command line runs in the session's job,
    /// which kills every process in it when the session ends (on the remote
    /// machine, 2026-10-17: KILL_ON_JOB_CLOSE | BREAKAWAY_OK), and the
    /// components must outlive it. Everywhere else they stay in the caller's job, so a job
    /// that cleans up after a run (a CI runner's) still catches a component
    /// a test failed to stop.
    /// </param>
    public static unsafe (int Pid, DateTimeOffset StartedAt) Start(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        string logPath,
        bool breakAway = false)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environment);

        using var log = File.OpenHandle(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        if (!PInvoke.SetHandleInformation(log, (uint)HANDLE_FLAGS.HANDLE_FLAG_INHERIT, HANDLE_FLAGS.HANDLE_FLAG_INHERIT))
        {
            throw new Win32Exception();
        }

        nuint size = 0;
        PInvoke.InitializeProcThreadAttributeList(default, 1, 0, &size);
        var attributes = (LPPROC_THREAD_ATTRIBUTE_LIST)NativeMemory.Alloc(size);
        try
        {
            if (!PInvoke.InitializeProcThreadAttributeList(attributes, 1, 0, &size))
            {
                throw new Win32Exception();
            }

            try
            {
                var logHandle = (HANDLE)log.DangerousGetHandle();
                if (!PInvoke.UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributeHandleList, &logHandle, (nuint)sizeof(HANDLE), null, (nuint*)null))
                {
                    throw new Win32Exception();
                }

                var startup = new STARTUPINFOEXW
                {
                    StartupInfo = new STARTUPINFOW
                    {
                        cb = (uint)sizeof(STARTUPINFOEXW),
                        dwFlags = STARTUPINFOW_FLAGS.STARTF_USESTDHANDLES,
                        hStdOutput = logHandle,
                        hStdError = logHandle,
                    },
                    lpAttributeList = attributes,
                };

                var commandLine = (CommandLine.Quote(executable) + " " + CommandLine.Join(arguments) + "\0").ToCharArray();
                var block = EnvironmentBlock(environment);
                PROCESS_INFORMATION information;
                fixed (char* commandLinePointer = commandLine)
                fixed (char* blockPointer = block)
                fixed (char* directory = workingDirectory)
                fixed (char* application = executable)
                {
                    if (!PInvoke.CreateProcess(
                            application,
                            commandLinePointer,
                            null,
                            null,
                            true,
                            CreationFlags(breakAway),
                            blockPointer,
                            directory,
                            (STARTUPINFOW*)&startup,
                            &information))
                    {
                        var error = Marshal.GetLastPInvokeError();
                        throw breakAway && error == AccessDenied
                            ? new Win32Exception(error, $"Could not start {executable} outside this session's job: the job does not allow it, and inside it the component would end with the session.")
                            : new Win32Exception(error, $"Could not start {executable}");
                    }
                }

                try
                {
                    System.Runtime.InteropServices.ComTypes.FILETIME created, exited, kernel, user;
                    if (!PInvoke.GetProcessTimes(information.hProcess, &created, &exited, &kernel, &user))
                    {
                        throw new Win32Exception();
                    }

                    var ticks = ((long)(uint)created.dwHighDateTime << 32) | (uint)created.dwLowDateTime;
                    return ((int)information.dwProcessId, new DateTimeOffset(DateTime.FromFileTimeUtc(ticks)));
                }
                finally
                {
                    PInvoke.CloseHandle(information.hThread);
                    PInvoke.CloseHandle(information.hProcess);
                }
            }
            finally
            {
                PInvoke.DeleteProcThreadAttributeList(attributes);
            }
        }
        finally
        {
            NativeMemory.Free(attributes);
        }
    }

    /// <summary>How <see cref="Start"/> creates the process.</summary>
    internal static PROCESS_CREATION_FLAGS CreationFlags(bool breakAway) =>
        PROCESS_CREATION_FLAGS.CREATE_NO_WINDOW
            | PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT
            | PROCESS_CREATION_FLAGS.EXTENDED_STARTUPINFO_PRESENT
            | (breakAway ? PROCESS_CREATION_FLAGS.CREATE_BREAKAWAY_FROM_JOB : 0);

    /// <summary>
    /// Sends Ctrl+Break to the console of process <paramref name="pid"/>,
    /// which a .NET host takes as a request to stop gracefully. A process can
    /// only signal the console it is attached to, so this runs in a short-lived
    /// helper (<c>simple signal-stop</c>) rather than in the command line itself.
    /// </summary>
    /// <returns>Whether the signal was delivered.</returns>
    public static async Task<bool> RequestStopAsync(int pid, CancellationToken cancellationToken)
    {
        var (fileName, prefix) = Self();
        var start = new ProcessStartInfo(fileName) { CreateNoWindow = true, UseShellExecute = false };
        foreach (var argument in prefix)
        {
            start.ArgumentList.Add(argument);
        }

        start.ArgumentList.Add(SignalStopCommand.Name);
        start.ArgumentList.Add(pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var helper = Process.Start(start) ?? throw new InvalidOperationException("Could not start the signal helper.");
        await helper.WaitForExitAsync(cancellationToken);
        return helper.ExitCode == 0;
    }

    /// <summary>Runs inside the helper: attaches to the target's console and sends Ctrl+Break to everything on it.</summary>
    public static int SendCtrlBreak(int pid)
    {
        PInvoke.FreeConsole();
        if (!PInvoke.AttachConsole((uint)pid))
        {
            return 1;
        }

        // The helper shares the target's console now and receives the signal
        // too (.NET reports Ctrl+Break as SIGQUIT). It ignores it, and waits
        // for it so that it does not exit with the handler gone before the
        // signal arrives: Windows delivers it on a new thread in every process
        // on the console. Registered only after attaching: a handler
        // registered before FreeConsole was not called (seen 2026-10-17, the
        // helper ended with STATUS_CONTROL_C_EXIT).
        using var received = new ManualResetEventSlim();
        using var ignore = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, context =>
        {
            context.Cancel = true;
            received.Set();
        });

        if (!PInvoke.GenerateConsoleCtrlEvent(PInvoke.CTRL_BREAK_EVENT, 0))
        {
            return 2;
        }

        received.Wait(TimeSpan.FromSeconds(5));
        return 0;
    }

    /// <summary>Whether process <paramref name="pid"/> is the one that was recorded: alive, with the same start time.</summary>
    public static Process? Find(int pid, DateTimeOffset startedAt)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            if (!process.HasExited && new DateTimeOffset(process.StartTime.ToUniversalTime()) == startedAt)
            {
                return process;
            }

            process.Dispose();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Gone, or not ours to inspect.
        }

        return null;
    }

    private static (string FileName, IReadOnlyList<string> Prefix) Self()
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("The command line's own path is unknown.");
        // Run as `dotnet simple.dll` (the tests do), the helper must be too.
        return string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? (path, [typeof(ChildProcess).Assembly.Location])
            : (path, []);
    }

    private static char[] EnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var block = new StringBuilder();
        foreach (var (key, value) in environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            block.Append(key).Append('=').Append(value).Append('\0');
        }

        return block.Append('\0').ToString().ToCharArray();
    }
}
