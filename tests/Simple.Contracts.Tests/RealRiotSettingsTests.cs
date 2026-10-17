using Simple.Contracts.Tests.Targets;

namespace Simple.Contracts.Tests;

/// <summary>
/// When the contracts refuse to run against a real RIoT. These never call
/// anything: they only read settings files in a temporary folder.
/// </summary>
public sealed class RealRiotSettingsTests : IDisposable
{
    private const string Complete = """
        {
          "url": "http://riot.invalid:8888",
          "callApiKey": "placeholder-key",
          "vehicleKey": "BROKERX-00000000000000000000000000000000",
          "mapId": 26,
          "vehicleStation": 151,
          "reachableStation": 155
        }
        """;

    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("simple-contracts-");

    public void Dispose() => root.Delete(recursive: true);

    [Fact]
    public void OnCiTheRealRiotIsRefused()
    {
        var file = Write(root.FullName, Complete);
        Assert.SkipWhen(RealRiotSettings.EnclosingGitClone(file) is not null, "临时目录在一个 git 克隆里，测不了这一条。");

        var refused = Assert.Throws<RealRiotRefusedException>(() => RealRiotSettings.Load(file, name => name == "CI" ? "true" : null));

        Assert.Contains("CI", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>Not only this clone: a settings file inside any git clone could be committed with its credentials.</summary>
    [Theory]
    [InlineData("folder")]
    [InlineData("file")]
    public void ASettingsFileInsideAnyGitCloneIsRefused(string gitKind)
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

        var file = Write(clone.CreateSubdirectory("deeper").FullName, Complete);

        var refused = Assert.Throws<RealRiotRefusedException>(() => RealRiotSettings.Load(file, _ => null));

        Assert.Contains(clone.FullName, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASettingsFileOutsideEveryCloneIsRead()
    {
        var file = Write(root.FullName, Complete);
        Assert.SkipWhen(RealRiotSettings.EnclosingGitClone(file) is not null, "临时目录在一个 git 克隆里，测不了这一条。");

        var settings = RealRiotSettings.Load(file, _ => null);

        Assert.Equal(new Uri("http://riot.invalid:8888"), settings.Url);
        Assert.Equal(26, settings.Facts.MapId);
        Assert.Equal(155, settings.Facts.SecondReachableStation);
        Assert.Null(settings.Facts.UnreachableStation);
    }

    [Theory]
    [InlineData("url")]
    [InlineData("callApiKey")]
    [InlineData("vehicleKey")]
    [InlineData("mapId")]
    public void ASettingsFileWithoutWhatTheContractsNeedIsRefused(string missing)
    {
        var lines = Complete.Split('\n').Where(l => !l.Contains($"\"{missing}\"", StringComparison.Ordinal));
        var file = Write(root.FullName, string.Join('\n', lines));
        Assert.SkipWhen(RealRiotSettings.EnclosingGitClone(file) is not null, "临时目录在一个 git 克隆里，测不了这一条。");

        var refused = Assert.Throws<RealRiotRefusedException>(() => RealRiotSettings.Load(file, _ => null));

        Assert.Contains(missing, refused.Message, StringComparison.Ordinal);
    }

    private static string Write(string folder, string json)
    {
        var file = Path.Combine(folder, "riot-contracts.json");
        File.WriteAllText(file, json);
        return file;
    }
}
