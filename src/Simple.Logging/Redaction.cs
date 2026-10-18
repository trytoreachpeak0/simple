using System.Text;
using System.Text.RegularExpressions;

namespace Simple.Logging;

/// <summary>
/// Takes credentials out of text bound for a program log (ADR 0034): the
/// value goes, what it belongs to stays, so the line still says what happened.
/// When in doubt it removes too much rather than too little: a log line that
/// lost a harmless value is still useful, a credential in it is not. A
/// pairing code is only taken out where it is named as one: lot, EQP and
/// station values are often digits too and must stay readable.
/// </summary>
/// <remarks>
/// Every pattern starts only where a word or a marker starts, so the time
/// grows with the length of the text, not with its square: redaction runs on
/// the thread that logs, a UI thread included. Each pattern also has a time
/// limit; a text that runs into it is replaced as a whole.
/// </remarks>
public static partial class Redaction
{
    /// <summary>What a removed value is replaced with.</summary>
    public const string Mask = "***";

    /// <summary>What replaces a whole text whose redaction ran out of time.</summary>
    public const string TimedOut = "*** (not shown: redacting it took too long)";

    /// <summary>
    /// Parts of a name that mark its value as a credential: passwords, secrets,
    /// tokens, keys, and the pairing code an engineer checks at the vehicle
    /// before a terminal's key is accepted (ADR 0025). A name that ends in
    /// <c>key</c> counts too (<c>Simple:Riot:Key</c>, <c>SigningKey</c>), one
    /// that only has it inside does not (<c>ServerKeyName</c>).
    /// </summary>
    private const string SecretName =
        @"(?:pass(?:word|wd)|pwd|secret|token|api[_-]?key|access[_-]?key|account[_-]?key|private[_-]?key|credential|pairing[_-]?code|cookie)";

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // A backstop against a pattern going slow on some text nobody foresaw, not a budget.
    private const int TimeoutMilliseconds = 1000;

    /// <summary>Whether a property, setting or field with this name holds a credential.</summary>
    public static bool IsSecretName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        try
        {
            TestHook?.Invoke();
            // An exception type such as TokenExpiredException names what went wrong, not a value.
            return !name.EndsWith("Exception", StringComparison.OrdinalIgnoreCase)
                && (SecretNamePart().IsMatch(name) || name.EndsWith("key", StringComparison.OrdinalIgnoreCase));
        }
        catch (RegexMatchTimeoutException)
        {
            // When in doubt, a credential.
            return true;
        }
    }

    /// <summary>Runs at the start of every redaction and name check, so tests can make them fail.</summary>
    internal static Action? TestHook { get; set; }

    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            TestHook?.Invoke();

            // Each pattern runs only where a plain search finds what it needs,
            // so most lines skip most patterns. Whole values first, then values
            // found by the name in front of them.
            var named = SecretNamePart().IsMatch(text) || text.Contains("key", StringComparison.OrdinalIgnoreCase);
            if (text.Contains("PRIVATE KEY-----", StringComparison.OrdinalIgnoreCase))
            {
                text = PrivateKeyBlock().Replace(text, Mask);
            }

            if (text.Contains("eyJ", StringComparison.Ordinal))
            {
                text = Jwt().Replace(text, Mask);
            }

            if (text.Contains("gh", StringComparison.Ordinal))
            {
                text = GitHubToken().Replace(text, Mask);
            }

            if (named && text.Contains('"', StringComparison.Ordinal))
            {
                text = JsonField().Replace(text, KeepNameAndQuotes);
            }

            if (text.Contains("authorization", StringComparison.OrdinalIgnoreCase))
            {
                text = AuthorizationHeader().Replace(text, KeepName);
            }

            if (text.Contains("bearer", StringComparison.OrdinalIgnoreCase))
            {
                text = BearerToken().Replace(text, KeepName);
            }

            if (text.Contains("cookie", StringComparison.OrdinalIgnoreCase))
            {
                text = CookieHeader().Replace(text, KeepName);
            }

            if (text.Contains("://", StringComparison.Ordinal) && text.Contains('@', StringComparison.Ordinal))
            {
                text = UrlPassword().Replace(text, KeepName);
            }

            if (text.Contains('/', StringComparison.Ordinal) && text.Contains('@', StringComparison.Ordinal))
            {
                text = OracleLogin().Replace(text, KeepName);
            }

            if (named && (text.AsSpan().IndexOfAny('=', ':') >= 0 || text.Contains("--", StringComparison.Ordinal)))
            {
                text = NamedValues(text);
            }

            return text.Contains("pairing", StringComparison.OrdinalIgnoreCase) || text.Contains("配对码", StringComparison.Ordinal)
                ? PairingCodeInWords().Replace(text, KeepName)
                : text;
        }
        catch (RegexMatchTimeoutException)
        {
            return TimedOut;
        }
    }

    private static string KeepName(Match match) => match.Groups["name"].Value + Mask;

    // A JSON string stays a string, so the line stays valid JSON.
    private static string KeepNameAndQuotes(Match match) =>
        match.Groups["name"].Value + (match.Groups["value"].Value.StartsWith('"') ? $"\"{Mask}\"" : Mask);

    /// <summary>
    /// <c>name=value</c>, <c>name: value</c> and <c>--name value</c> where the
    /// name says it is a credential (<see cref="IsSecretName"/>): connection
    /// strings, command-line settings, query strings, headers, rendered objects.
    /// </summary>
    private static string NamedValues(string text)
    {
        StringBuilder? output = null;
        var copied = 0;
        foreach (Match match in Named().Matches(text))
        {
            var start = match.Groups["compound"].Success
                ? CompoundValueStart(match)
                : IsSecretName(match.Groups["key"].Value) ? match.Index + match.Length : -1;
            if (match.Index < copied || start < 0 || start >= text.Length
                || string.CompareOrdinal(text, start, Mask, 0, Mask.Length) == 0)
            {
                continue;
            }

            var end = ValueEnd(text, match, start);
            if (end == start)
            {
                continue;
            }

            output ??= new StringBuilder(text.Length);
            output.Append(text, copied, start - copied).Append(Mask);
            copied = end;
        }

        return output is null ? text : output.Append(text, copied, text.Length - copied).ToString();
    }

    /// <summary>
    /// In a word such as <c>Simple:Mes:Password:hunter2</c>, where the value
    /// starts: after the first colon whose part before it names a credential;
    /// -1 when no part does.
    /// </summary>
    private static int CompoundValueStart(Match match)
    {
        var word = match.Value;
        if (!SecretNamePart().IsMatch(word) && !word.Contains("key", StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        // A name holds no colon, so whether a prefix names a credential depends
        // only on its last part: each part is checked once, not every prefix again.
        var part = 0;
        for (var colon = word.IndexOf(':', StringComparison.Ordinal); colon >= 0; colon = word.IndexOf(':', colon + 1))
        {
            if (IsSecretName(word[part..colon]))
            {
                return match.Index + colon + 1;
            }

            part = colon + 1;
        }

        return -1;
    }

    /// <summary>
    /// Where the value that starts at <paramref name="start"/> ends. Every scan
    /// here is bounded, so many values on one long line stay cheap.
    /// </summary>
    private static int ValueEnd(string text, Match match, int start)
    {
        const int MaxQuoted = 1000;
        const int SpaceWindow = 200;

        // Quoted as in connection strings: "a""b", 'a''b', {a}}b} (ODBC).
        var close = text[start] switch { '"' => '"', '\'' => '\'', '{' => '}', _ => '\0' };
        if (close != '\0')
        {
            var limit = Math.Min(text.Length, start + MaxQuoted);
            for (var i = start + 1; i < limit && text[i] is not ('\r' or '\n'); i++)
            {
                if (text[i] == close)
                {
                    if (i + 1 < text.Length && text[i + 1] == close)
                    {
                        i++;
                        continue;
                    }

                    return i + 1;
                }
            }

            // Not closed nearby: everything up to there goes.
            var cut = start + 1;
            while (cut < limit && text[cut] is not ('\r' or '\n'))
            {
                cut++;
            }

            return cut;
        }

        // A value ends at a space or a separator (a query string, a rendered
        // object, a command line)...
        var end = start;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not ('&' or ',' or ';' or ')' or '}' or ']' or '"' or '\'' or '<' or '>'))
        {
            end++;
        }

        // ...except in a connection string, where it runs to the next ; (or the
        // end of the line after the last setting) and may hold spaces.
        if (!match.Groups["equals"].Success || end == text.Length || !char.IsWhiteSpace(text[end]) || text[end] is '\r' or '\n')
        {
            return end;
        }

        var afterSemicolon = PreviousNonSpace(text, match.Index) == ';';
        var window = Math.Min(text.Length, end + SpaceWindow);
        for (var i = end; i < window; i++)
        {
            if (text[i] == ';')
            {
                return i;
            }

            if (text[i] is '\r' or '\n')
            {
                return afterSemicolon ? i : end;
            }
        }

        return afterSemicolon && window == text.Length ? window : end;
    }

    private static char PreviousNonSpace(string text, int index)
    {
        for (var i = index - 1; i >= 0 && i >= index - 20; i--)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return text[i];
            }
        }

        return '\0';
    }

    [GeneratedRegex(SecretName, Options, TimeoutMilliseconds)]
    private static partial Regex SecretNamePart();

    // -----BEGIN RSA PRIVATE KEY----- ... -----END RSA PRIVATE KEY-----, the markers too;
    // a block cut off before its END line goes to the end of the text.
    [GeneratedRegex(@"-----BEGIN [A-Z ]{0,40}PRIVATE KEY-----(?:.*?-----END [A-Z ]{0,40}PRIVATE KEY-----|.*)", Options | RegexOptions.Singleline, TimeoutMilliseconds)]
    private static partial Regex PrivateKeyBlock();

    // A JSON Web Token: base64url header and payload, both JSON objects, so both begin with eyJ.
    [GeneratedRegex(@"(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]*\.eyJ[A-Za-z0-9_-]*\.[A-Za-z0-9_-]*", RegexOptions.CultureInvariant, TimeoutMilliseconds)]
    private static partial Regex Jwt();

    // GitHub tokens (ghp_, gho_, ghu_, ghs_, ghr_, github_pat_), for example a NuGet feed credential.
    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})", RegexOptions.CultureInvariant, TimeoutMilliseconds)]
    private static partial Regex GitHubToken();

    // "password": "value", "pairingCode": 482913 or "signingKey": "value"; names up to 200 characters.
    [GeneratedRegex(@"(?<name>""(?:[^""\r\n]{0,100}?" + SecretName + @"[^""\r\n]{0,100}|[^""\r\n]{0,200}?key)""\s*:\s*)(?<value>""(?:[^""\\\r\n]|\\.)*""|[^,}\]\s]+)", Options, TimeoutMilliseconds)]
    private static partial Regex JsonField();

    // Authorization: <scheme> <credentials>, any scheme (Bearer, Basic, Token, ApiKey...).
    [GeneratedRegex(@"(?<name>(?<![\w-])(?:Proxy-)?Authorization\s*[:=]\s*(?:(?:Bearer|Basic|Token|ApiKey|Digest|Negotiate|NTLM|Hawk|SharedKey|Key)\s+)?)[^\s,;""']+", Options, TimeoutMilliseconds)]
    private static partial Regex AuthorizationHeader();

    // Bearer <token> outside a header, when what follows looks like a token, not a word.
    [GeneratedRegex(@"(?<name>(?<![\w-])Bearer\s+)(?!\*\*\*)[A-Za-z0-9\-._~+/]{16,}=*", Options, TimeoutMilliseconds)]
    private static partial Regex BearerToken();

    // Cookie: and Set-Cookie: carry session values; the whole header value goes.
    [GeneratedRegex(@"(?<name>(?<![\w-])(?:Set-)?Cookie\s*:\s*)[^\r\n]+", Options, TimeoutMilliseconds)]
    private static partial Regex CookieHeader();

    // scheme://user:password@host: the password goes, the user and host stay. The password
    // runs to the last @ before the host, so an unescaped @ in it does not leak the rest.
    [GeneratedRegex(@"(?<name>(?<![A-Za-z0-9+.\-])[A-Za-z][A-Za-z0-9+.\-]{0,30}://[^/\s:@]{1,200}:)[^\s/]+(?=@[^@\s/]*(?:[/?#\s]|$))", Options, TimeoutMilliseconds)]
    private static partial Regex UrlPassword();

    // user/password@database, the login Oracle's own tools take (the MES is Oracle), also
    // inside jdbc:oracle:thin:user/password@...: the password goes, the user and database
    // stay. The password may hold / and @ and runs to the @ before the database: a
    // descriptor "(DESCRIPTION=...", or //host, host or [address], then a port, a /service
    // or the word's end. A login starts only at the start of a word (after a space, a quote
    // or a parenthesis) or after thin:, and the password stops at those same characters,
    // so no part of a long line is scanned by more than one try.
    [GeneratedRegex(@"(?<name>(?:(?<=^|[\s""'(])|(?<=thin:))[A-Za-z][\w$#]{0,127}/)(?>[^\s@""'(]{1,200})(?:@(?>[^\s@""'(]{1,200})){0,4}?(?=@(?:\(|(?://)?(?:[\w.-]{1,253}|\[[0-9A-Fa-f:.]{2,45}\])(?::\d{1,5})?(?:[/\s;,)""'?]|$)))", Options, TimeoutMilliseconds)]
    private static partial Regex OracleLogin();

    // A name followed by = or :, a --option followed by a space, or a word with colons and
    // no separator after it (Password:hunter2, Simple:Mes:Password:hunter2). The name is taken
    // whole (atomic), so the match is tried once per word; IsSecretName decides about it. The
    // space around a separator never spans lines: a value is never looked for on the next one.
    [GeneratedRegex(@"(?<![\w.:-])(?:(?<key>(?>[\w.-]+(?::[\w.-]+)*))[ \t]*(?:(?<equals>=)|:)[ \t]*|(?<key>(?>--[\w.-]+(?::[\w.-]+)*))[ \t]+(?=[^\s-])|(?<compound>(?>[\w.-]+(?::[\w.-]+)+)))", Options, TimeoutMilliseconds)]
    private static partial Regex Named();

    // The pairing code written out rather than named by a property: "pairing code 482913", "配对码：482913".
    [GeneratedRegex(@"(?<name>(?:pairing[\s_-]*code|配对码)(?:\s*[:=：])?\s*)\d+", Options, TimeoutMilliseconds)]
    private static partial Regex PairingCodeInWords();
}
