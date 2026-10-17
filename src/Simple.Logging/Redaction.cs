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

    private const int TimeoutMilliseconds = 250;

    /// <summary>Whether a property, setting or field with this name holds a credential.</summary>
    public static bool IsSecretName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        // An exception type such as TokenExpiredException names what went wrong, not a value.
        return !name.EndsWith("Exception", StringComparison.OrdinalIgnoreCase)
            && (SecretNamePart().IsMatch(name) || name.EndsWith("key", StringComparison.OrdinalIgnoreCase));
    }

    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            // Whole values first, then values found by the name in front of them.
            text = PrivateKeyBlock().Replace(text, Mask);
            text = Jwt().Replace(text, Mask);
            text = GitHubToken().Replace(text, Mask);
            text = JsonField().Replace(text, KeepNameAndQuotes);
            text = AuthorizationHeader().Replace(text, KeepName);
            text = BearerToken().Replace(text, KeepName);
            text = CookieHeader().Replace(text, KeepName);
            text = UrlPassword().Replace(text, KeepName);
            text = NamedValues(text);
            return PairingCodeInWords().Replace(text, KeepName);
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
            if (match.Index < copied || !IsSecretName(match.Groups["key"].Value))
            {
                continue;
            }

            var start = match.Index + match.Length;
            if (string.CompareOrdinal(text, start, Mask, 0, Mask.Length) == 0)
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

    /// <summary>Where the value that starts at <paramref name="start"/> ends.</summary>
    private static int ValueEnd(string text, Match match, int start)
    {
        // Quoted as in connection strings: "a""b", 'a''b', {a}}b} (ODBC).
        var close = text[start] switch { '"' => '"', '\'' => '\'', '{' => '}', _ => '\0' };
        if (close != '\0')
        {
            for (var i = start + 1; i < text.Length && text[i] is not ('\r' or '\n'); i++)
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

            return LineEnd(text, start);
        }

        // In a connection string a value runs to the next ; and may hold spaces;
        // elsewhere (a query string, a rendered object, a command line) it ends
        // at a space or a separator.
        var line = LineEnd(text, start);
        var semicolon = text.IndexOf(';', start, line - start);
        var inConnectionString = match.Groups["equals"].Success
            && (semicolon >= 0 || PreviousNonSpace(text, match.Index) == ';');
        if (inConnectionString)
        {
            return semicolon >= 0 ? semicolon : line;
        }

        var end = start;
        while (end < line && !char.IsWhiteSpace(text[end]) && text[end] is not ('&' or ',' or ';' or ')' or '}' or ']' or '"' or '\'' or '<' or '>'))
        {
            end++;
        }

        return end;
    }

    private static int LineEnd(string text, int start)
    {
        var end = text.IndexOfAny(['\r', '\n'], start);
        return end < 0 ? text.Length : end;
    }

    private static char PreviousNonSpace(string text, int index)
    {
        for (var i = index - 1; i >= 0; i--)
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

    // "password": "value" or "pairingCode": 482913; the name is at most 100 characters each side.
    [GeneratedRegex(@"(?<name>""[^""\r\n]{0,100}?" + SecretName + @"[^""\r\n]{0,100}""\s*:\s*)(?<value>""(?:[^""\\\r\n]|\\.)*""|[^,}\]\s]+)", Options, TimeoutMilliseconds)]
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

    // A name followed by = or :, or a --option followed by a space. The name is taken whole
    // (atomic), so the match is tried once per word; IsSecretName decides about it.
    [GeneratedRegex(@"(?<![\w.:-])(?<key>(?>[\w.-]+(?::[\w.-]+)*))(?:\s*(?:(?<equals>=)|:)\s*|(?<=(?<![\w.:-])--[\w.:-]+)\s+(?=[^\s-]))", Options, TimeoutMilliseconds)]
    private static partial Regex Named();

    // The pairing code written out rather than named by a property: "pairing code 482913", "配对码：482913".
    [GeneratedRegex(@"(?<name>(?:pairing[\s_-]*code|配对码)\s*[:=：]?\s*)\d+", Options, TimeoutMilliseconds)]
    private static partial Regex PairingCodeInWords();
}
