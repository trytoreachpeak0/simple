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

            // Asked first with no room, then with as much as it says it needs.
            // A list too small is not filled in at all (ERROR_MORE_DATA), and
            // the list can grow between two calls, so this is tried a few times.
            RM_PROCESS_INFO[] found = [];
            for (var attempt = 0; attempt < 3; attempt++)
            {
                uint needed;
                var count = (uint)found.Length;
                uint reasons;
                WIN32_ERROR result;
                fixed (RM_PROCESS_INFO* list = found)
                {
                    result = PInvoke.RmGetList(session, &needed, &count, list, &reasons);
                }

                if (result == WIN32_ERROR.ERROR_SUCCESS)
                {
                    var holders = new List<string>();
                    for (var i = 0; i < count; i++)
                    {
                        var pid = (int)found[i].Process.dwProcessId;
                        holders.Add($"{NameOf(pid) ?? found[i].strAppName.ToString()} (pid {pid})");
                    }

                    return holders;
                }

                if (result != WIN32_ERROR.ERROR_MORE_DATA)
                {
                    return [];
                }

                found = new RM_PROCESS_INFO[needed];
            }

            return [];
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
