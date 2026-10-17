using System.Text.RegularExpressions;

namespace Simple.Logging;

/// <summary>
/// Takes credentials out of text bound for a program log (ADR 0034): the
/// value goes, what it belongs to stays, so the line still says what happened.
/// When in doubt it removes too much rather than too little: a log line that
/// lost a harmless value is still useful, a credential in it is not.
/// </summary>
public static partial class Redaction
{
    /// <summary>What a removed value is replaced with.</summary>
    public const string Mask = "***";

    /// <summary>
    /// Parts of a name that mark its value as a credential: passwords, secrets,
    /// tokens, keys, and the pairing code an engineer checks at the vehicle
    /// before a terminal's key is accepted (ADR 0025). A setting such as
    /// <c>Simple:Mes:Password</c> or a property such as <c>AccessToken</c> matches.
    /// </summary>
    private const string SecretName =
        @"(?:pass(?:word|wd)|pwd|secret|token|api[_-]?key|access[_-]?key|account[_-]?key|private[_-]?key|credential|pairing[_-]?code)";

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Whether a property or setting with this name holds a credential.</summary>
    public static bool IsSecretName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return SecretNamePart().IsMatch(name);
    }

    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        // Whole values first, then values found by the name in front of them.
        text = PrivateKeyBlock().Replace(text, Mask);
        text = Jwt().Replace(text, Mask);
        text = GitHubToken().Replace(text, Mask);
        text = JsonField().Replace(text, KeepNameAndQuotes);
        text = AuthorizationScheme().Replace(text, KeepName);
        text = ConnectionStringPassword().Replace(text, KeepName);
        text = NamedSetting().Replace(text, KeepName);
        text = UrlPassword().Replace(text, KeepName);
        return PairingCodeInWords().Replace(text, KeepName);
    }

    private static string KeepName(Match match) => match.Groups["name"].Value + Mask;

    // A JSON string stays a string, so the line stays valid JSON.
    private static string KeepNameAndQuotes(Match match) =>
        match.Groups["name"].Value + (match.Groups["value"].Value.StartsWith('"') ? $"\"{Mask}\"" : Mask);

    [GeneratedRegex(SecretName, Options)]
    private static partial Regex SecretNamePart();

    // -----BEGIN RSA PRIVATE KEY----- ... -----END RSA PRIVATE KEY-----, the markers too.
    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----.*?-----END [A-Z ]*PRIVATE KEY-----", Options | RegexOptions.Singleline)]
    private static partial Regex PrivateKeyBlock();

    // A JSON Web Token: base64url header and payload, both JSON objects, so both begin with eyJ.
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]*\.eyJ[A-Za-z0-9_-]*\.[A-Za-z0-9_-]*", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();

    // GitHub tokens (ghp_, gho_, ghu_, ghs_, ghr_, github_pat_), for example a NuGet feed credential.
    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubToken();

    // "password": "value" or "pairingCode": 482913.
    [GeneratedRegex(@"(?<name>""[^""\r\n]*" + SecretName + @"[^""\r\n]*""\s*:\s*)(?<value>""(?:[^""\\\r\n]|\\.)*""|[^,}\]\s]+)", Options)]
    private static partial Regex JsonField();

    // Authorization: Bearer <token> / Basic <user:password in base64>.
    [GeneratedRegex(@"(?<name>\b(?:Bearer|Basic)\s+)[A-Za-z0-9\-._~+/]+=*", Options)]
    private static partial Regex AuthorizationScheme();

    // Password=... in a connection string: the value runs to the next ; or the end of the line
    // (it may hold spaces), or is quoted.
    [GeneratedRegex(@"(?<name>(?<![\w-])(?:password|pwd)\s*=\s*)(?:""[^""\r\n]*""|'[^'\r\n]*'|[^;\r\n]+)", Options)]
    private static partial Regex ConnectionStringPassword();

    // Any other name=value or name: value whose name says it is a credential: command-line
    // settings (--Simple:Mes:Password=...), query strings (api_key=...), headers (X-Api-Key: ...).
    [GeneratedRegex(@"(?<name>[\w.:-]*" + SecretName + @"[\w-]*\s*[=:]\s*)(?!\*\*\*)(?:""[^""\r\n]*""|'[^'\r\n]*'|[^\s;&,""']+)", Options)]
    private static partial Regex NamedSetting();

    // scheme://user:password@host: the password goes, the user and host stay.
    [GeneratedRegex(@"(?<name>\b[a-z][a-z0-9+.\-]*://[^/\s:@]+:)(?<value>[^@\s/]+)(?=@)", Options)]
    private static partial Regex UrlPassword();

    // The pairing code written out rather than named by a property: "pairing code 482913", "配对码：482913".
    [GeneratedRegex(@"(?<name>(?:pairing[\s_-]*code|配对码)\s*[:=：]?\s*)\d+", Options)]
    private static partial Regex PairingCodeInWords();
}
