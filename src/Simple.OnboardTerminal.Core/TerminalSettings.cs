using System.Net;
using System.Text.Json;
using Simple.Protocol;

namespace Simple.OnboardTerminal.Core;

/// <summary>
/// What an onboard terminal keeps on disk (ADR 0024): which vehicle it is on,
/// where its server is, the pin of the server's certificate, where the
/// vehicle's IO module is, and where its own key is (ADR 0025). A JSON file
/// outside the repository, for example
/// <c>{"vehicleId": "vehicle-1", "server": "https://127.0.0.1:5181", "serverCertificatePin": "&lt;64 hex digits&gt;", "ioModule": "127.0.0.1:1502"}</c>.
/// The key itself is in the Windows key store (<see cref="TerminalKey"/>), never in the file.
/// </summary>
/// <param name="IoModule">
/// The IO module's address and port. ADR 0024 left open whether the terminal
/// keeps it or the server sends it with the vehicle list; the terminal keeps
/// it, next to the server's address (#70). Without it the terminal reads no
/// IO module and reports that it cannot.
/// </param>
/// <param name="KeyName">The name of the terminal's key; <see cref="DefaultKeyName"/> when the file does not say.</param>
/// <param name="KeyStore">user (the default: the account the terminal runs as) or machine (needs an administrator; tests and the local system over SSH).</param>
/// <param name="KeyProvider">auto (the default: the TPM when the computer has one, otherwise the software key store) or software.</param>
public sealed record TerminalSettings(
    string? VehicleId,
    string Server,
    string? ServerCertificatePin,
    string? IoModule = null,
    string? KeyName = null,
    string? KeyStore = null,
    string? KeyProvider = null)
{
    /// <summary>Where the server is when the file does not say: this machine.</summary>
    public const string DefaultServer = "https://127.0.0.1:5181";

    /// <summary>The terminal's key when the file names none: one terminal per computer.</summary>
    public const string DefaultKeyName = "Simple-OnboardTerminal";

    public string EffectiveKeyName => KeyName is { Length: > 0 } name ? name : DefaultKeyName;

    public bool MachineKey => string.Equals(KeyStore, "machine", StringComparison.OrdinalIgnoreCase);

    public bool TryTpm => !string.Equals(KeyProvider, "software", StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Web);

    /// <summary>The file's place when none is given on the command line.</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Simple", "OnboardTerminal", "terminal.json");

    public Uri ServerUri => new(Server);

    /// <summary>The IO module's endpoint; null when none is configured.</summary>
    public IPEndPoint? IoModuleEndpoint => IoModule is { Length: > 0 } module ? IPEndPoint.Parse(module) : null;

    public static TerminalSettings Load(string path)
    {
        var file = JsonSerializer.Deserialize<File>(System.IO.File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException($"{path} is empty.");
        return new TerminalSettings(
            file.VehicleId, file.Server is { Length: > 0 } server ? server : DefaultServer, file.ServerCertificatePin, file.IoModule, file.KeyName, file.KeyStore, file.KeyProvider);
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(new File(VehicleId, Server, ServerCertificatePin, IoModule, KeyName, KeyStore, KeyProvider), JsonOptions));
    }

    /// <summary>What keeps the terminal from starting, one line each; empty when nothing does.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(VehicleId))
        {
            problems.Add("vehicleId is missing: the vehicle this terminal is on, as the server's vehicle list names it.");
        }

        if (!Uri.TryCreate(Server, UriKind.Absolute, out var server) || server.Scheme != Uri.UriSchemeHttps)
        {
            problems.Add($"server '{Server}' is not an https:// address. The terminal connects over TLS only.");
        }

        if (!CertificatePin.IsWellFormed(ServerCertificatePin))
        {
            problems.Add(
                "serverCertificatePin is missing or not 64 hex digits. Without it the terminal cannot tell its server from any other, so it does not connect; "
                + "the server's status (GET /api/v1/status, onboardTerminalEndpoint.certificatePin) shows it.");
        }

        if (IoModule is { Length: > 0 } module && (!IPEndPoint.TryParse(module, out var endpoint) || endpoint.Port == 0))
        {
            problems.Add($"ioModule '{module}' is not an IP address and port, for example 192.168.71.150:502.");
        }

        if (KeyName is { Length: > 0 } key && (key.Length > 100 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
        {
            problems.Add($"keyName '{key}' is not up to 100 letters, digits, '-', '_' or '.'.");
        }

        if (KeyStore is { Length: > 0 } store && !string.Equals(store, "user", StringComparison.OrdinalIgnoreCase) && !MachineKey)
        {
            problems.Add($"keyStore '{store}' is neither user nor machine.");
        }

        if (KeyProvider is { Length: > 0 } provider && !string.Equals(provider, "auto", StringComparison.OrdinalIgnoreCase) && TryTpm)
        {
            problems.Add($"keyProvider '{provider}' is neither auto nor software.");
        }

        return problems;
    }

    private sealed record File(string? VehicleId, string? Server, string? ServerCertificatePin, string? IoModule, string? KeyName, string? KeyStore, string? KeyProvider);
}
