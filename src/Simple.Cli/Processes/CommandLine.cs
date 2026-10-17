using System.Text;

namespace Simple.Cli.Processes;

/// <summary>
/// Builds a Windows command line that a .NET program (or any program using
/// the C runtime's rules) splits back into exactly the given arguments. The
/// same rules <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>
/// follows; the framework keeps its implementation internal.
/// </summary>
public static class CommandLine
{
    public static string Join(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(Quote));

    public static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            return argument;
        }

        var quoted = new StringBuilder().Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            // Backslashes are literal unless they precede a quote: then each
            // one is doubled, and the quote itself is escaped.
            quoted.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes).Append(c);
            backslashes = 0;
        }

        // Backslashes before the closing quote are doubled so they do not escape it.
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
