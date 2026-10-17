namespace Simple.Logging;

/// <summary>Where a program writes its log file and how much of it is kept.</summary>
/// <param name="Directory">The program's log directory; the program creates it.</param>
/// <param name="FileSizeLimitBytes">
/// A file that has reached this size is closed and the next one begun; the
/// entry that crosses the limit is still written whole.
/// </param>
/// <param name="RetainedFileCount">
/// How many files are kept, the one being written included; beginning one more
/// deletes the oldest. The log takes about <see cref="FileSizeLimitBytes"/>
/// times this at most.
/// </param>
public sealed record ProgramLogSettings(string Directory, long FileSizeLimitBytes, int RetainedFileCount);
