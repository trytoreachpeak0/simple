using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.RestartManager;

namespace Simple.Cli.Processes;

/// <summary>
/// Which processes hold a file, as the Windows Restart Manager reports them:
/// those that have it open or loaded (a running program's DLLs). A process
/// the caller may not inspect, such as a virus scanner, may be missing.
/// </summary>
public static class FileHolders
{
    /// <summary>One line per process, for example <c>Simple.Server (pid 1234)</c>; empty when none is found or the question fails.</summary>
    public static unsafe IReadOnlyList<string> Of(string path)
    {
        uint session;
        var key = stackalloc char[(int)PInvoke.CCH_RM_SESSION_KEY + 1];
        if (PInvoke.RmStartSession(&session, 0, key) != WIN32_ERROR.ERROR_SUCCESS)
        {
            return [];
        }

        try
        {
            fixed (char* file = path)
            {
                var files = new PCWSTR(file);
                if (PInvoke.RmRegisterResources(session, 1, &files, 0, null, 0, null) != WIN32_ERROR.ERROR_SUCCESS)
                {
                    return [];
                }
            }

            const int Room = 16;
            var found = stackalloc RM_PROCESS_INFO[Room];
            uint needed;
            uint count = Room;
            uint reasons;
            var result = PInvoke.RmGetList(session, &needed, &count, found, &reasons);
            if (result is not (WIN32_ERROR.ERROR_SUCCESS or WIN32_ERROR.ERROR_MORE_DATA))
            {
                return [];
            }

            var holders = new List<string>();
            for (var i = 0; i < Math.Min(count, (uint)Room); i++)
            {
                var pid = (int)found[i].Process.dwProcessId;
                holders.Add($"{NameOf(pid) ?? found[i].strAppName.ToString()} (pid {pid})");
            }

            return holders;
        }
        finally
        {
            PInvoke.RmEndSession(session);
        }
    }

    private static string? NameOf(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
