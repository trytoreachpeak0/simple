using System.Text.Json;
using Simple.Protocol;

namespace Simple.OnboardTerminal.Core;

/// <summary>
/// What an onboard terminal keeps on disk (ADR 0024): which vehicle it is on,
/// where its server is, and the pin of the server's certificate. A JSON file
/// outside the repository, for example
/// <c>{"vehicleId": "vehicle-1", "server": "https://127.0.0.1:5181", "serverCertificatePin": "&lt;64 hex digits&gt;"}</c>.
/// </summary>
public sealed record TerminalSettings(string? VehicleId, string Server, string? ServerCertificatePin)
{
    /// <summary>Where the server is when the file does not say: this machine.</summary>
    public const string DefaultServer = "https://127.0.0.1:5181";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Web);

    /// <summary>The file's place when none is given on the command line.</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Simple", "OnboardTerminal", "terminal.json");

    public Uri ServerUri => new(Server);

    public static TerminalSettings Load(string path)
    {
        var file = JsonSerializer.Deserialize<File>(System.IO.File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException($"{path} is empty.");
        return new TerminalSettings(file.VehicleId, file.Server is { Length: > 0 } server ? server : DefaultServer, file.ServerCertificatePin);
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(new File(VehicleId, Server, ServerCertificatePin), JsonOptions));
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

        return problems;
    }

    private sealed record File(string? VehicleId, string? Server, string? ServerCertificatePin);
}
