using System.Diagnostics;
using System.Globalization;
using System.Net;
using Simple.Contracts.Tests.Targets;

namespace Simple.Contracts.Tests;

/// <summary>
/// When the contracts refuse to run against a real RIoT, and that a real
/// RIoT is never sent a write. These never reach any RIoT: they read
/// settings files in a temporary folder, and the one request that would go
/// out is to <c>riot.invalid</c> and is stopped before it is sent.
/// </summary>
public sealed class RealRiotSettingsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 21, 0, 0, TimeSpan.FromHours(8));

    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("simple-contracts-");

    public void Dispose() => root.Delete(recursive: true);

    private static string Complete(DateTimeOffset? runUntil = null) => $$"""
        {
          "url": "http://riot.invalid:8888",
          "callApiKey": "placeholder-key",
          "vehicleKey": "BROKERX-00000000000000000000000000000000",
          "mapId": 26,
          "vehicleStation": 151,
          "runUntil": "{{(runUntil ?? Now.AddHours(2)).ToString("O", CultureInfo.InvariantCulture)}}",
          "reachableStation": 155
        }
        """;

    [Fact]
    public void OnCiTheRealRiotIsRefused()
    {
        var file = WriteOutsideAnyClone(Complete());

        var refused = Assert.Throws<RealRiotRefusedException>(() => RealRiotSettings.Load(file, name => name == "CI" ? "true" : null, Now));

        Assert.Contains("CI", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>Not only this clone: a settings file inside any git clone could be committed with its credentials.</summary>
    [Theory]
    [InlineData("folder")]
    [InlineData("file")]
    public void ASettingsFileInsideAnyGitCloneIsRefused(string gitKind)
    {
        var clone = MakeClone(gitKind);
        var file = Write(clone.CreateSubdirectory("deeper").FullName, Complete());

        var refused = Assert.Throws<RealRiotRefusedException>(() => RealRiotSettings.Load(file, _ => null, Now));

        Assert.Contains(clone.FullName, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A junction outside every clone that leads into one does not get the file past the check.</summary>
    [Fact]
    public void ASettingsFileReachedThroughAJunctionIntoACloneIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "目录联接只在 Windows 上测。");
        var clone = MakeClone("folder");
        var inside = clone.CreateSubdirectory("settings");
        Write(inside.FullName, Complete());
        var junction = Path.Combine(root.FullName, "outside-looking");
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", junction, inside.FullName]) { RedirectStandardOutput = true, UseShellExecute = false })!)
        {
            mklink.WaitForExit();
            Assert.SkipUnless(mklink.ExitCode == 0, "建不了目录联接。");
        }

        try
        {
            var refused = Assert.Throws<RealRiotRefusedException>(
                () => RealRiotSettings.Load(Path.Combine(junction, "riot-contracts.json"), _ => null, Now));

            Assert.Contains(clone.FullName, refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            // Removes the junction itself, not what it points at; a recursive delete through it is refused.
            Directory.Delete(junction);
        }
    }

    [Fact]
    public void ASettingsFileOutsideEveryCloneIsRead()
    {
        var file = WriteOutsideAnyClone(Complete());

        var settings = RealRiotSettings.Load(file, _ => null, Now);

        Assert.Equal(new Uri("http://riot.invalid:8888"), settings.Url);
        Assert.Equal(26, settings.Facts.MapId);
        Assert.Equal(155, settings.Facts.SecondReachableStation);
        Assert.Null(settings.Facts.UnreachableStation);
    }

    /// <summary>runUntil confirms one run: once it has passed, or when it lies more than twelve hours ahead, the file is refused.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(13)]
    public void ARunUntilThatHasPassedOrLiesTooFarAheadIsRefused(int hoursFromNow)
    {
        var file = WriteOutsideAnyClone(Complete(Now.AddHours(hoursFromNow)));

        var refused = Assert.Throws<RealRiotRefusedException>(() => RealRiotSettings.Load(file, _ => null, Now));

        Assert.Contains("runUntil", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("url")]
    [InlineData("callApiKey")]
    [InlineData("vehicleKey")]
    [InlineData("mapId")]
    [InlineData("runUntil")]
    public void ASettingsFileWithoutWhatTheContractsNeedIsRefused(string missing)
    {
        var lines = Complete().Split('\n').Where(l => !l.Contains($"\"{missing}\"", StringComparison.Ordinal));
        var file = WriteOutsideAnyClone(string.Join('\n', lines));

        var refused = Assert.Throws<RealRiotRefusedException>(() => RealRiotSettings.Load(file, _ => null, Now));

        Assert.Contains(missing, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A contract that forgets RequireWrites still cannot create an order on a real RIoT: the request is stopped before it is sent.</summary>
    [Fact]
    public async Task ARealRiotIsNeverSentAWrite()
    {
        var facts = new RiotTargetFacts("BROKERX-00000000000000000000000000000000", 26, 151, 155, 155, null, null, null, null, null, null);
        await using var target = new RealRiotTarget(new RealRiotSettings(new Uri("http://riot.invalid:8888"), "placeholder-key", null, null, facts, "test"));

        var refused = await Assert.ThrowsAnyAsync<Exception>(
            () => target.Session.Order.CreateMoveOrderAsync("sc-guard", facts.VehicleKey, 26, 155, cancellationToken: TestContext.Current.CancellationToken));

        Assert.IsType<RealRiotWriteRefusedException>(Innermost(refused));
    }

    [Theory]
    [InlineData("POST", "/api/order/v1/add/byDefaultMissions", false)]
    [InlineData("POST", "/api/task/v1/order/command/order-1", false)]
    [InlineData("POST", "/api/device/v1/command/sync/service/BROKERX-1/triggerEmergency", false)]
    [InlineData("POST", "/api/task/vehicles/updateVehicleIntegrationLevel", false)]
    [InlineData("POST", "/api/order/v1/orderRecordPriorityExec", false)]
    [InlineData("PUT", "/api/auth/v1/admin/refreshToken", false)]
    [InlineData("DELETE", "/api/task/v1/route/", false)]
    [InlineData("GET", "/api/task/v1/task/execActionCommand/BROKERX-1/1/2/3", false)]
    [InlineData("GET", "/api/task/v1/task/clearVehicleAndCancelOrderTask/BROKERX-1", false)]
    [InlineData("GET", "/api/task/v1/task/clearVehicle/BROKERX-1", false)]
    [InlineData("GET", "/api/task/v1/task/clearAllVehicle", false)]
    [InlineData("GET", "/api/task/v1/task/clearOrderSequence/BROKERX-1", false)]
    [InlineData("GET", "/api/task/vehicles/setting/get", false)]
    [InlineData("GET", "/api/order/v1/orderRecord/detailByUpperId/x/extra", false)]
    [InlineData("GET", "/api/order/v1/orderRecord/detailByUpperId/x", true)]
    [InlineData("GET", "/api/order/v1/orderRecord/detailByOrderId/order-1", true)]
    [InlineData("GET", "/api/order/v1/orderRecord", true)]
    [InlineData("GET", "/api/imap/v1/mapInfo/getALLMapInfoExcludeMapJson", true)]
    [InlineData("GET", "/api/imap/v1/mapInfo/stations/26", true)]
    [InlineData("GET", "/api/imap/v1/mapInfo/edges/26", true)]
    [InlineData("GET", "/api/imap/v1/mapResource/removedEdge/26", true)]
    [InlineData("GET", "/api/imap/v1/mapResource/removedStation/26", true)]
    [InlineData("GET", "/api/task/vehicles/getVehicleInfoByDeviceKey", true)]
    [InlineData("GET", "/api/task/v1/task/getVehicleInfo/BROKERX-1", true)]
    [InlineData("POST", "/api/auth/v1/admin/login", true)]
    [InlineData("POST", "/api/task/v1/route/getRouteCostsBy", true)]
    [InlineData("POST", "/api/task/v1/route/queryNearEnd", true)]
    [InlineData("POST", "/api/task/v1/route/queryNearestStart", true)]
    public async Task TheGuardLetsThroughOnlyReads(string method, string path, bool allowed)
    {
        var inner = new Recording();
        using var client = new HttpClient(new ReadOnlyGuard(inner)) { BaseAddress = new Uri("http://riot.invalid") };
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        if (allowed)
        {
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        }
        else
        {
            await Assert.ThrowsAsync<RealRiotWriteRefusedException>(() => client.SendAsync(request, TestContext.Current.CancellationToken));
        }

        Assert.Equal(allowed ? 1 : 0, inner.Sent);
    }

    /// <summary>The synchronous send goes through the guard too.</summary>
    [Fact]
    public void TheGuardAlsoStopsASynchronousSend()
    {
        var inner = new Recording();
        using var client = new HttpClient(new ReadOnlyGuard(inner)) { BaseAddress = new Uri("http://riot.invalid") };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/order/v1/add/byDefaultMissions");

        Assert.Throws<RealRiotWriteRefusedException>(() => client.Send(request, TestContext.Current.CancellationToken));
        Assert.Equal(0, inner.Sent);
    }

    private static Exception Innermost(Exception e) => e.InnerException is { } inner ? Innermost(inner) : e;

    private DirectoryInfo MakeClone(string gitKind)
    {
        var clone = root.CreateSubdirectory("some-other-clone");
        if (gitKind == "folder")
        {
            clone.CreateSubdirectory(".git");
        }
        else
        {
            // A worktree's .git is a file.
            File.WriteAllText(Path.Combine(clone.FullName, ".git"), "gitdir: elsewhere");
        }

        return clone;
    }

    private string WriteOutsideAnyClone(string json)
    {
        var file = Write(root.FullName, json);
        Assert.SkipWhen(RealRiotSettings.EnclosingGitClone(file) is not null, "临时目录在一个 git 克隆里，测不了这一条。");
        return file;
    }

    private static string Write(string folder, string json)
    {
        var file = Path.Combine(folder, "riot-contracts.json");
        File.WriteAllText(file, json);
        return file;
    }

    private sealed class Recording : HttpMessageHandler
    {
        public int Sent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
