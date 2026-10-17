using System.Text.Json;
using System.Text.Json.Serialization;

namespace Simple.Cli;

/// <summary>
/// What a command ends with. With <c>--json</c>, stdout carries exactly one
/// JSON object, <c>{"exitCode", "error", "result"}</c>, whatever happened;
/// without it, <see cref="Lines"/> go to stdout and <see cref="Error"/> to
/// stderr. Progress always goes to stderr, so stdout stays parseable.
/// </summary>
public sealed record Outcome(int ExitCode, string? Error, object? Result, IReadOnlyList<string> Lines)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Web)
    {
        WriteIndented = true,
        // stdout is read by people and programs, never embedded in HTML.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static Outcome Ok(object? result, params IReadOnlyList<string> lines) => new(ExitCodes.Success, null, result, lines);

    public static Outcome Fail(int exitCode, string error, object? result = null, params IReadOnlyList<string> lines) =>
        new(exitCode, error, result, lines);

    public int Write(bool json, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new { ExitCode, Error, Result }, JsonOptions));
        }
        else
        {
            foreach (var line in Lines)
            {
                stdout.WriteLine(line);
            }

            if (Error is not null)
            {
                stderr.WriteLine(Error);
            }
        }

        return ExitCode;
    }
}
