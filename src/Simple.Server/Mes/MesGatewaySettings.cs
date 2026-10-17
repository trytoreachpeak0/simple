using Microsoft.Extensions.Options;

namespace Simple.Server.Mes;

/// <summary>How the server reaches the MES gateway, read from <c>Simple:MesGateway</c>.</summary>
public sealed class MesGatewaySettings
{
    public const string Section = "Simple:MesGateway";

    /// <summary>The gateway's base address.</summary>
    public Uri? Url { get; set; }

    /// <summary>The wait between two fetches of the gateway's snapshot.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The longest wait between retries while the gateway cannot be reached (ADR 0008: 30 s).</summary>
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long one fetch may take before the gateway counts as unreachable.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);
}

public sealed class MesGatewaySettingsValidator : IValidateOptions<MesGatewaySettings>
{
    public ValidateOptionsResult Validate(string? name, MesGatewaySettings options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        const string section = MesGatewaySettings.Section;
        if (options.Url is not { IsAbsoluteUri: true } url || url.Scheme is not ("http" or "https"))
        {
            failures.Add($"{section}:Url '{options.Url?.OriginalString}' is not an http or https address of the MES gateway, for example http://127.0.0.1:5190.");
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
