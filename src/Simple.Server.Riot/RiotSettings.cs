using Microsoft.Extensions.Options;

namespace Simple.Server.Riot;

/// <summary>
/// How the server reaches the fleet manager, read from <c>Simple:Riot</c>.
/// The repository's defaults point at the fake on loopback; a real RIoT's
/// address and call key come from outside the repository (repository
/// CLAUDE.md, Safety). Without <see cref="Url"/> the server runs without a
/// fleet manager and says so in its status.
/// </summary>
public sealed class RiotSettings
{
    public const string Section = "Simple:Riot";

    /// <summary>RIoT's base address, for example the fake's http://127.0.0.1:5200; null for none.</summary>
    public Uri? Url { get; set; }

    /// <summary>The call key from RIoT's web page, sent as the Bearer (BC-AUTH-002).</summary>
    public string? CallApiKey { get; set; }

    /// <summary>The wait between two connection checks while RIoT answers.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The longest wait between retries while RIoT cannot be reached (ADR 0008: 30 s).</summary>
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long one call may take before RIoT counts as unreachable.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public bool Configured => Url is not null;
}

public sealed class RiotSettingsValidator : IValidateOptions<RiotSettings>
{
    public ValidateOptionsResult Validate(string? name, RiotSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        const string section = RiotSettings.Section;
        if (options.Url is { } url)
        {
            if (!url.IsAbsoluteUri || url.Scheme is not ("http" or "https"))
            {
                failures.Add($"{section}:Url '{url.OriginalString}' is not an http or https address of the fleet manager, for example http://127.0.0.1:5200.");
            }

            if (string.IsNullOrWhiteSpace(options.CallApiKey))
            {
                failures.Add($"{section}:CallApiKey is empty. RIoT answers every call without its call key with HTTP 401.");
            }
        }

        foreach (var (key, value) in new[]
                 {
                     ("PollInterval", options.PollInterval),
                     ("RetryMaxDelay", options.RetryMaxDelay),
                     ("RequestTimeout", options.RequestTimeout),
                 })
        {
            if (value <= TimeSpan.Zero)
            {
                failures.Add($"{section}:{key} must be longer than zero.");
            }
        }

        if (options.RetryMaxDelay < options.PollInterval)
        {
            failures.Add($"{section}:RetryMaxDelay must not be shorter than {section}:PollInterval.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
