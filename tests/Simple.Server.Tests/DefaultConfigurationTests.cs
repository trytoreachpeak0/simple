using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Simple.Server.Configuration;

namespace Simple.Server.Tests;

/// <summary>
/// The repository rule "default configuration never points at anything
/// real": every appsettings*.json in the repository may name only loopback
/// hosts, LocalDB or placeholders such as "&lt;set outside the repository&gt;".
/// </summary>
public sealed partial class DefaultConfigurationTests
{
    [Fact]
    public void NoAppsettingsFileInTheRepositoryPointsAtARealHost()
    {
        var root = RepositoryRoot();
        var files = Directory.EnumerateFiles(root, "appsettings*.json", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or ".git"))
            .ToList();

        // Finding no file at all would prove nothing.
        Assert.Contains(files, f => f.EndsWith(Path.Combine("Simple.Server", "appsettings.json"), StringComparison.Ordinal));
        var findings = files
            .SelectMany(f => FindRealHosts(File.ReadAllText(f)).Select(finding => $"{Path.GetRelativePath(root, f)}: {finding}"))
            .ToList();
        Assert.True(findings.Count == 0, "Default configuration points at real hosts:\n" + string.Join('\n', findings));
    }

    [Theory]
    [InlineData("""{ "Urls": "http://0.0.0.0:5180" }""")]
    [InlineData("""{ "Kestrel": { "Endpoints": { "Http": { "Url": "http://*:80" } } } }""")]
    [InlineData("""{ "Riot": { "BaseUrl": "http://10.20.30.40:8080/api" } }""")]
    [InlineData("""{ "Mes": { "Endpoint": "https://mes.example.com/query" } }""")]
    [InlineData("""{ "Mes": { "Host": "factory01" } }""")]
    [InlineData("""{ "Riot": { "Address": "192.168.1.20:8080" } }""")]
    [InlineData("""{ "ConnectionStrings": { "Simple": "Server=factory01;Integrated Security=true" } }""")]
    [InlineData("""{ "ConnectionStrings": { "Simple": "Data Source=tcp:10.0.0.5,1433;Integrated Security=true" } }""")]
    [InlineData("""{ "ConnectionStrings": { "Simple": "Server=.\\SQLEXPRESS;Integrated Security=true" } }""")]
    [InlineData("""{ "Targets": [ "http://127.0.0.1:1", "http://agv01:2000" ] }""")]
    public void ARealHostIsFound(string json) => Assert.NotEmpty(FindRealHosts(json));

    [Theory]
    [InlineData("""{ "Urls": "http://127.0.0.1:5180;http://[::1]:5180;http://localhost:5180" }""")]
    [InlineData("""{ "ConnectionStrings": { "Simple": "Server=(localdb)\\MSSQLLocalDB;Integrated Security=true" } }""")]
    [InlineData("""{ "Riot": { "BaseUrl": "<set outside the repository>" } }""")]
    [InlineData("""{ "AllowedHosts": "*", "Logging": { "LogLevel": { "Default": "Information" } } }""")]
    public void LoopbackLocaldbAndPlaceholdersPass(string json) => Assert.Empty(FindRealHosts(json));

    /// <summary>Every host named in the JSON that is neither loopback nor LocalDB, as "path = value: host".</summary>
    private static List<string> FindRealHosts(string json)
    {
        var findings = new List<string>();
        using var document = JsonDocument.Parse(
            json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        Walk(document.RootElement, "", findings);
        return findings;
    }

    private static void Walk(JsonElement element, string path, List<string> findings)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value, path.Length == 0 ? property.Name : $"{path}:{property.Name}", findings);
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, $"{path}:{index++}", findings);
                }

                break;
            case JsonValueKind.String:
                var value = element.GetString()!;
                findings.AddRange(HostsIn(path, value)
                    .Where(host => !IsAllowed(host))
                    .Distinct()
                    .Select(host => $"{path} = '{value}': '{host}' is neither loopback nor LocalDB"));
                break;
        }
    }

    private static IEnumerable<string> HostsIn(string path, string value)
    {
        if (value.StartsWith('<') && value.EndsWith('>'))
        {
            yield break;
        }

        var isConnectionString = path.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase)
            || ConnectionStringServer().IsMatch(value);
        if (isConnectionString)
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = value };
            foreach (var key in new[] { "server", "data source", "address", "addr", "network address", "host" })
            {
                if (builder.TryGetValue(key, out var server) && server?.ToString() is { Length: > 0 } text)
                {
                    yield return SqlServerHost(text);
                }
            }
        }

        foreach (Match url in UrlHost().Matches(value))
        {
            yield return url.Groups["host"].Value;
        }

        foreach (Match ip in Ipv4().Matches(value))
        {
            yield return ip.Value;
        }

        var name = path[(path.LastIndexOf(':') + 1)..];
        if (!isConnectionString && !value.Contains("://", StringComparison.Ordinal) && HostSetting().IsMatch(name))
        {
            yield return HostPort().Match(value).Groups["host"].Value;
        }
    }

    /// <summary>The machine part of a SQL Server name: "tcp:host\instance,port" gives "host".</summary>
    private static string SqlServerHost(string server)
    {
        var colon = server.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0 && !server.StartsWith('['))
        {
            server = server[(colon + 1)..];
        }

        return server.Split('\\', ',')[0].Trim();
    }

    private static bool IsAllowed(string host) =>
        ServerSettingsValidator.IsLoopbackHost(host) || string.Equals(host, "(localdb)", StringComparison.OrdinalIgnoreCase);

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "simple.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"No simple.slnx above {AppContext.BaseDirectory}.");
    }

    [GeneratedRegex(@"(?:^|;)\s*(?:server|data source|address|addr|network address)\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionStringServer();

    [GeneratedRegex(@"[a-z][a-z0-9+.-]*://(?<host>\[[^\]]*\]|[^:/\s;,""']+)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlHost();

    [GeneratedRegex(@"\b\d{1,3}(?:\.\d{1,3}){3}\b")]
    private static partial Regex Ipv4();

    [GeneratedRegex(@"(host|hostname|server|address|endpoint|url|uri)$", RegexOptions.IgnoreCase)]
    private static partial Regex HostSetting();

    [GeneratedRegex(@"^(?<host>\[[^\]]*\]|[^:]*)")]
    private static partial Regex HostPort();
}
