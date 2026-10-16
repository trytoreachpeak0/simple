namespace Simple.ProtocolProbe.Server;

/// <summary>
/// Command-line options (<c>--name value</c>). Every default is loopback or a
/// directory under the current user's profile, never a real machine.
/// The server's HTTP/2 keepalive ping is off unless <c>--ping-delay</c> is
/// given (seconds), which is Kestrel's own default.
/// </summary>
internal sealed record ProbeServerOptions(
    string Bind,
    int Port,
    string DataDirectory,
    string LogFile,
    string Tls,
    string KeyAlgorithm,
    bool RequireClientCertificate,
    TimeSpan CommandInterval,
    TimeSpan StaleAfter,
    TimeSpan FreshWithin,
    TimeSpan CloneWindow,
    TimeSpan? PingDelay,
    TimeSpan PingTimeout,
    bool ConsoleLog)
{
    public const string LogTemplate =
        "{Timestamp:yyyy-MM-ddTHH:mm:ss.fffzzz} {Level:u3} {Message:l}{NewLine}{Exception}";

    public static ProbeServerOptions From(IConfiguration config)
    {
        var data = config["data"] ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "simple-probe", "server");
        var log = config["log"] ?? Path.Combine(
            data, "logs", $"server-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
        return new ProbeServerOptions(
            Bind: config["bind"] ?? "127.0.0.1",
            Port: int.Parse(config["port"] ?? "5443", System.Globalization.CultureInfo.InvariantCulture),
            DataDirectory: data,
            LogFile: log,
            Tls: config["tls"] ?? "12",
            KeyAlgorithm: config["key-alg"] ?? "ecdsa",
            RequireClientCertificate: (config["client-cert"] ?? "require") == "require",
            CommandInterval: Seconds(config["command-interval"], 10),
            StaleAfter: Seconds(config["stale-after"], 10),
            FreshWithin: Seconds(config["fresh-within"], 2),
            CloneWindow: Seconds(config["clone-window"], 10),
            PingDelay: config["ping-delay"] is null ? null : Seconds(config["ping-delay"], 0),
            PingTimeout: Seconds(config["ping-timeout"], 20),
            ConsoleLog: (config["console-log"] ?? "true") == "true");
    }

    private static TimeSpan Seconds(string? value, double fallback) =>
        TimeSpan.FromSeconds(value is null ? fallback : double.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
}
