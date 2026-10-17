using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Display;

namespace Simple.Logging;

/// <summary>
/// The program log every SIMPLE program writes (ADR 0034): Serilog behind
/// <see cref="Microsoft.Extensions.Logging"/>, into size-limited files in the
/// program's log directory, credentials redacted (<see cref="Redaction"/>),
/// unhandled exceptions recorded (<see cref="UnhandledExceptionLog"/>).
/// </summary>
public static class ProgramLog
{
    /// <summary>
    /// The settings: <c>Simple:Logs:Directory</c>,
    /// <c>Simple:Logs:FileSizeLimitBytes</c>, <c>Simple:Logs:RetainedFileCount</c>.
    /// </summary>
    public const string Section = "Simple:Logs";

    public const long DefaultFileSizeLimitBytes = 10 * 1024 * 1024;

    public const int DefaultRetainedFileCount = 10;

    private const string Template = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>Where <paramref name="program"/> logs when its settings do not say: <c>%ProgramData%\Simple\&lt;program&gt;\logs</c>.</summary>
    public static string DefaultDirectory(string program) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Simple", program, "logs");

    /// <summary>Reads <see cref="Section"/>; what it leaves out takes the defaults.</summary>
    /// <exception cref="InvalidDataException">A limit is not a whole number above 0.</exception>
    public static ProgramLogSettings ReadSettings(IConfiguration configuration, string program)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var directory = configuration[$"{Section}:Directory"] is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : DefaultDirectory(program);
        return new ProgramLogSettings(
            directory,
            Positive(configuration, "FileSizeLimitBytes", DefaultFileSizeLimitBytes),
            (int)Positive(configuration, "RetainedFileCount", DefaultRetainedFileCount));
    }

    /// <summary>
    /// Makes the program log the program's only log output: removes the other
    /// providers (console, debug, event log), whose output would not be
    /// redacted, and writes the log file, plus the console when
    /// <paramref name="console"/>. The settings are read from
    /// <paramref name="configuration"/> when the first logger is made, so
    /// sources added to it until then count.
    /// </summary>
    public static ILoggingBuilder AddProgramLog(this ILoggingBuilder logging, string program, IConfiguration configuration, bool console)
    {
        ArgumentNullException.ThrowIfNull(logging);
        logging.ClearProviders();
        logging.Services.AddSingleton<ILoggerProvider>(_ => CreateProvider(program, ReadSettings(configuration, program), console));
        return logging;
    }

    /// <summary>A logger provider writing <paramref name="program"/>'s log, and its console output when <paramref name="console"/>.</summary>
    public static ProgramLogProvider CreateProvider(string program, ProgramLogSettings settings, bool console)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var formatter = new RedactingFormatter(new MessageTemplateTextFormatter(Template, CultureInfo.InvariantCulture));
        var configuration = new LoggerConfiguration()
            // Microsoft.Extensions.Logging filters (Logging:LogLevel) decide what reaches this logger.
            .MinimumLevel.Verbose()
            .Enrich.With<RedactingEnricher>()
            .WriteTo.File(
                formatter,
                Path.Combine(settings.Directory, program + ".log"),
                fileSizeLimitBytes: settings.FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: settings.RetainedFileCount);
        if (console)
        {
            configuration.WriteTo.Console(formatter);
        }

        return new ProgramLogProvider(new SerilogLoggerProvider(configuration.CreateLogger(), dispose: true));
    }

    /// <summary>
    /// The log files in a program's log directory, oldest first: <c>&lt;program&gt;.log</c>,
    /// then <c>&lt;program&gt;_001.log</c>, <c>&lt;program&gt;_002.log</c>… as the
    /// file is rolled. One program logs into a directory.
    /// </summary>
    public static IReadOnlyList<string> Files(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory, "*.log")
            .Select(path => (Path: path, Sequence: Sequence(Path.GetFileNameWithoutExtension(path))))
            .OrderBy(file => file.Sequence)
            .ThenBy(file => file.Path, StringComparer.Ordinal)
            .Select(file => file.Path)
            .ToList();
    }

    private static int Sequence(string name)
    {
        var separator = name.LastIndexOf('_');
        return separator >= 0 && int.TryParse(name.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
            ? sequence
            : 0;
    }

    private static long Positive(IConfiguration configuration, string name, long fallback)
    {
        var key = $"{Section}:{name}";
        if (configuration[key] is not { Length: > 0 } text)
        {
            return fallback;
        }

        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is > 0 and <= int.MaxValue
            ? value
            : throw new InvalidDataException($"{key} '{text}' is not a whole number above 0.");
    }
}
