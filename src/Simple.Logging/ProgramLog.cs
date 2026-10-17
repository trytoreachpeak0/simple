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
public static partial class ProgramLog
{
    /// <summary>
    /// The settings: <c>Simple:Logs:Directory</c>,
    /// <c>Simple:Logs:FileSizeLimitBytes</c>, <c>Simple:Logs:RetainedFileCount</c>.
    /// </summary>
    public const string Section = "Simple:Logs";

    public const long DefaultFileSizeLimitBytes = 10 * 1024 * 1024;

    public const int DefaultRetainedFileCount = 10;

    // A line is the prefix, then the body; only the body is redacted (RedactingFormatter).
    private const string Prefix = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: ";

    private const string Body = "{Message:lj}{NewLine}{Exception}";

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
    /// <exception cref="InvalidDataException">The settings as they are now are invalid (<see cref="ReadSettings"/>).</exception>
    public static ILoggingBuilder AddProgramLog(this ILoggingBuilder logging, string program, IConfiguration configuration, bool console)
    {
        ArgumentNullException.ThrowIfNull(logging);
        _ = ReadSettings(configuration, program);
        logging.ClearProviders();
        logging.Services.AddSingleton<ILoggerProvider>(_ => CreateProvider(program, ReadSettings(configuration, program), console));
        return logging;
    }

    /// <summary>
    /// Says why a program refuses to start, then gives the exit code 2: on
    /// standard error for whoever started it, and in the program log, which is
    /// all there is to read when it runs as a service. The log is
    /// <paramref name="services"/>' while they are still there; otherwise one
    /// is opened from <paramref name="configuration"/> for this line, unless the
    /// log settings themselves are what is wrong.
    /// </summary>
    /// <param name="headline">The first line, for example "The server refuses to start: the configuration is invalid."</param>
    /// <param name="details">One line each, written as "  - detail" below the headline.</param>
    public static async Task<int> RefuseToStartAsync(
        string program, IConfiguration configuration, IServiceProvider? services, string headline, IReadOnlyList<string>? details = null)
    {
        ArgumentNullException.ThrowIfNull(headline);
        details ??= [];
        await Console.Error.WriteLineAsync(headline);
        foreach (var detail in details)
        {
            await Console.Error.WriteLineAsync($"  - {detail}");
        }

        var reason = details.Count == 0 ? headline : $"{headline} {string.Join("; ", details)}";
        try
        {
            if (services?.GetService<ILoggerFactory>() is { } loggers)
            {
                var logger = loggers.CreateLogger(typeof(ProgramLog).FullName!);
                Refused(logger, reason);
                return 2;
            }
        }
        catch (ObjectDisposedException)
        {
            // The host has already gone; open the log for this line below.
        }

        try
        {
            using var provider = CreateProvider(program, ReadSettings(configuration, program), console: false);
            var logger = provider.CreateLogger(typeof(ProgramLog).FullName!);
            Refused(logger, reason);
        }
        catch (InvalidDataException)
        {
            // The log settings are what is wrong: standard error is all there is.
        }

        return 2;
    }

    /// <summary>A logger provider writing <paramref name="program"/>'s log, and its console output when <paramref name="console"/>.</summary>
    public static ProgramLogProvider CreateProvider(string program, ProgramLogSettings settings, bool console)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var formatter = new RedactingFormatter(
            new MessageTemplateTextFormatter(Prefix, CultureInfo.InvariantCulture),
            new MessageTemplateTextFormatter(Body, CultureInfo.InvariantCulture));
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

        return new ProgramLogProvider(program, new SerilogLoggerProvider(configuration.CreateLogger(), dispose: true));
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

    /// <summary>
    /// Everything in a program's log directory, oldest file first, read the way
    /// <c>simple logs</c> reads it: while the program, or another process such as
    /// a virus scan, still has a file open.
    /// </summary>
    public static string ReadText(string directory)
    {
        var text = new System.Text.StringBuilder();
        foreach (var file in Files(directory))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text.Append(reader.ReadToEnd());
        }

        return text.ToString();
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

    [LoggerMessage(Level = LogLevel.Critical, Message = "{Reason}")]
    private static partial void Refused(Microsoft.Extensions.Logging.ILogger logger, string reason);
}
