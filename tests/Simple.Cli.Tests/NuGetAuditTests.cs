using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Simple.Cli.Tests;

/// <summary>
/// NuGet's audit under the repository's Directory.Build.props, which treats
/// warnings as errors. When the vulnerability data cannot be read (NU1900,
/// nuget.org unreachable from CI, run 39062968120), CI restores on with a
/// warning; a package with a known moderate or high vulnerability (NU1902,
/// NU1903) still fails restore there (#88). Restored as CI restores, with a
/// package from a local folder and the vulnerability data from a source of
/// this test on loopback: nothing leaves the machine.
/// </summary>
public sealed class NuGetAuditTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("simple-nuget-audit-").FullName;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OnCiVulnerabilityDataThatCannotBeReadIsAWarning()
    {
        var (exitCode, output) = await RestoreAsync(ci: true, severity: null);

        Assert.True(exitCode == 0, output);
        Assert.Contains("warning NU1900", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ElsewhereVulnerabilityDataThatCannotBeReadStillFailsRestore()
    {
        var (exitCode, output) = await RestoreAsync(ci: false, severity: null);

        Assert.True(exitCode != 0, output);
        Assert.Contains("error NU1900", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "NU1902")]
    [InlineData(2, "NU1903")]
    public async Task OnCiAKnownVulnerabilityStillFailsRestore(int severity, string code)
    {
        var (exitCode, output) = await RestoreAsync(ci: true, severity);

        Assert.True(exitCode != 0, output);
        Assert.Contains($"error {code}", output, StringComparison.Ordinal);
    }

    /// <param name="severity">The vulnerability the source reports for the package (1 moderate, 2 high); null: its data cannot be read.</param>
    private async Task<(int ExitCode, string Output)> RestoreAsync(bool ci, int? severity)
    {
        await using var source = new VulnerabilitySource(severity);
        var feed = Directory.CreateDirectory(Path.Combine(directory, "feed")).FullName;
        WritePackage(Path.Combine(feed, "simple.audit.probe.1.0.0.nupkg"));
        var project = Directory.CreateDirectory(Path.Combine(directory, "project")).FullName;
        var repository = CliEnvironment.FindRepository(AppContext.BaseDirectory);
        // The SDK the repository pins, whose NuGet CI restores with; without it the newest installed one would run.
        File.Copy(Path.Combine(repository, "global.json"), Path.Combine(directory, "global.json"), overwrite: true);
        File.WriteAllText(
            Path.Combine(project, "Directory.Build.props"),
            $"""<Project><Import Project="{Path.Combine(repository, "Directory.Build.props")}" /></Project>""");
        // The package comes from the folder only; the source on loopback gives
        // nothing but vulnerability data, as nuget.org does for CI.
        File.WriteAllText(
            Path.Combine(project, "nuget.config"),
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="feed" value="{feed}" />
                <add key="vulnerabilities" value="{source.Url}" allowInsecureConnections="true" />
              </packageSources>
              <auditSources>
                <clear />
              </auditSources>
              <packageSourceMapping>
                <packageSource key="feed">
                  <package pattern="*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        File.WriteAllText(
            Path.Combine(project, "probe.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <!-- No lock file to check: only the audit is under test. -->
                <RestorePackagesWithLockFile>false</RestorePackagesWithLockFile>
                <RestoreLockedMode>false</RestoreLockedMode>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Simple.Audit.Probe" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);

        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = project, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "restore", "probe.csproj", "-nologo", "--disable-build-servers" })
        {
            start.ArgumentList.Add(argument);
        }

        // Caches of its own: the vulnerability data is cached, and the probe must not reach the user's packages.
        start.Environment["NUGET_PACKAGES"] = Path.Combine(directory, "packages");
        start.Environment["NUGET_HTTP_CACHE_PATH"] = Path.Combine(directory, "http-cache");
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "true";
        start.Environment["DOTNET_NOLOGO"] = "true";
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        if (ci)
        {
            start.Environment["CI"] = "true";
        }
        else
        {
            start.Environment.Remove("CI");
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Cancel);
        var errors = process.StandardError.ReadToEndAsync(Cancel);
        await process.WaitForExitAsync(Cancel);
        return (process.ExitCode, await output + await errors);
    }

    /// <summary>A package with nothing in it but its manifest: enough to be restored and audited.</summary>
    private static void WritePackage(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("Simple.Audit.Probe.nuspec").Open(), new UTF8Encoding(false));
        writer.Write(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>Simple.Audit.Probe</id>
                <version>1.0.0</version>
                <authors>test</authors>
                <description>A package for NuGetAuditTests.</description>
              </metadata>
            </package>
            """);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A restore process still exiting; the temporary directory is the system's to clear.
        }
    }

    /// <summary>
    /// A NuGet V3 source on loopback with only the vulnerability resource
    /// (VulnerabilityInfo/6.7.0): it reports one vulnerability of the given
    /// severity for every version of the probe package below 2.0.0, or, with
    /// none, answers what NuGet cannot read.
    /// </summary>
    private sealed class VulnerabilitySource : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly int? severity;
        private readonly Task serving;

        public VulnerabilitySource(int? severity)
        {
            this.severity = severity;
            listener.Start();
            serving = ServeAsync();
        }

        private string Base => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";

        public string Url => $"{Base}/v3/index.json";

        private async Task ServeAsync()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                using (client)
                {
                    try
                    {
                        await AnswerAsync(client.GetStream());
                    }
                    catch (Exception e) when (e is IOException or OperationCanceledException)
                    {
                        // The client went away; the next request gets its answer.
                    }
                }
            }
        }

        private async Task AnswerAsync(NetworkStream stream)
        {
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var request = await reader.ReadLineAsync(stop.Token) ?? string.Empty;
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stop.Token)))
            {
            }

            var path = request.Split(' ') is [_, var target, ..] ? target : string.Empty;
            var (status, body) = path switch
            {
                "/v3/index.json" => ("200 OK", $$"""{"version":"3.0.0","resources":[{"@id":"{{Base}}/vulnerabilities/index.json","@type":"VulnerabilityInfo/6.7.0"}]}"""),
                "/vulnerabilities/index.json" when severity is null => ("200 OK", "{not json"),
                "/vulnerabilities/index.json" => ("200 OK", $$"""[{"@name":"base","@id":"{{Base}}/vulnerabilities/base.json","@updated":"2026-10-01T00:00:00Z"}]"""),
                "/vulnerabilities/base.json" => ("200 OK", $$"""{"simple.audit.probe":[{"url":"https://example.invalid/advisory","severity":{{severity}},"versions":"[1.0.0, 2.0.0)"}]}"""),
                _ => ("404 Not Found", "{}"),
            };
            var bytes = Encoding.UTF8.GetBytes(body);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), stop.Token);
            await stream.WriteAsync(bytes, stop.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            listener.Stop();
            await serving;
            stop.Dispose();
        }
    }
}
