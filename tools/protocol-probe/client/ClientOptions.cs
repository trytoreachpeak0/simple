using System.Globalization;
using System.Text.Json;

namespace Simple.ProtocolProbe.Client;

/// <summary>
/// The server address and pin come from <c>client.json</c> in the data
/// directory (outside the repository) or from <c>--server</c>/<c>--pin</c>;
/// there is no built-in default address.
/// </summary>
internal sealed record ClientOptions(
    string DataDirectory,
    string LogFile,
    string ServerUrl,
    string ServerPin,
    string Major,
    bool MinorExtra,
    TimeSpan ReportInterval,
    TimeSpan KeepAlivePingDelay,
    TimeSpan KeepAlivePingTimeout,
    string KeyProvider,
    string KeyAlgorithm,
    bool MachineKeyStore,
    string? ClaimVehicle,
    bool ConsoleLog)
{
    public const string LogTemplate =
        "{Timestamp:yyyy-MM-ddTHH:mm:ss.fffzzz} {Level:u3} {Message:l}{NewLine}{Exception}";

    public static Dictionary<string, string> ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"unexpected argument {args[i]}");
            }

            var name = args[i][2..];
            map[name] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
        }

        return map;
    }

    public static string DataDirectoryFrom(Dictionary<string, string> args) =>
        args.GetValueOrDefault("data") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "simple-probe", "client");

    public static ClientOptions From(Dictionary<string, string> args)
    {
        var data = DataDirectoryFrom(args);
        var configPath = Path.Combine(data, "client.json");
        var config = File.Exists(configPath)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(configPath)) ?? []
            : [];
        string Get(string name, string? fallback = null) =>
            args.GetValueOrDefault(name) ?? config.GetValueOrDefault(name) ?? fallback
            ?? throw new ArgumentException($"--{name} is required (or put \"{name}\" in {configPath})");
        TimeSpan Seconds(string name, double fallback) =>
            TimeSpan.FromSeconds(double.Parse(Get(name, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture));

        return new ClientOptions(
            DataDirectory: data,
            LogFile: Get("log", Path.Combine(data, "logs", $"client-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log")),
            ServerUrl: Get("server"),
            ServerPin: Get("pin").ToLowerInvariant(),
            Major: Get("major", "1"),
            MinorExtra: Get("minor-extra", "false") == "true",
            ReportInterval: Seconds("interval", 1),
            KeepAlivePingDelay: Seconds("keepalive", 10),
            KeepAlivePingTimeout: Seconds("keepalive-timeout", 10),
            KeyProvider: Get("key-provider", "software"),
            KeyAlgorithm: Get("key-alg", "ecdsa"),
            MachineKeyStore: Get("key-store", "user") == "machine",
            ClaimVehicle: args.GetValueOrDefault("claim-vehicle"),
            ConsoleLog: Get("console-log", "true") == "true");
    }
}
