namespace Simple.Cli.Remote;

/// <summary>
/// The temporary files and directories simple remote makes for one run (an
/// index, a bundle, a copy of the agent). Deleting one never replaces what
/// happened with an error of its own: a file that a git just killed by
/// Ctrl+C still holds is tried again briefly, then left in the temp
/// directory.
/// </summary>
internal static class Scratch
{
    public static void Delete(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else
                {
                    File.Delete(path);
                }

                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 5)
                {
                    return;
                }

                Thread.Sleep(100);
            }
        }
    }
}
