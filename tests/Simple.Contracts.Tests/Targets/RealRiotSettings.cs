using System.Text.Json;

namespace Simple.Contracts.Tests.Targets;

/// <summary>
/// A real RIoT a person selected for one run: a JSON file outside every git
/// clone, named by <see cref="RiotTarget.RealSettingsVariable"/>. Its
/// endpoint and credentials never enter the repository (repository
/// CLAUDE.md, Safety). Against it only the read-only contracts run.
/// </summary>
/// <remarks>
/// <code>
/// {
///   "url": "http://&lt;riot host&gt;:&lt;port&gt;",
///   "callApiKey": "&lt;the call key from RIoT's web page&gt;",
///   "username": "&lt;optional admin login&gt;", "password": "&lt;its password&gt;",
///   "vehicleKey": "BROKERX-…", "mapId": 26, "vehicleStation": 151, "reachableStation": 155,
///   "unreachableStation": null, "mapsSharingAStationId": [25, 26], "sharedStationId": 1
/// }
/// </code>
/// </remarks>
public sealed record RealRiotSettings(
    Uri Url,
    string CallApiKey,
    string? Username,
    string? Password,
    RiotTargetFacts Facts,
    string SettingsFile)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads the settings file, or refuses with the reason: on CI, when the
    /// file sits inside a git clone (any clone, not only this one), or when
    /// it lacks what the contracts need.
    /// </summary>
    public static RealRiotSettings Load(string path, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (string.Equals(environment("CI"), "true", StringComparison.OrdinalIgnoreCase))
        {
            throw Refused("CI 上不对真实 RIoT 跑契约测试：CI 机器在厂区网络里，能连到真实系统。");
        }

        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw Refused($"设置文件 '{full}' 不存在。");
        }

        if (EnclosingGitClone(full) is { } clone)
        {
            throw Refused($"设置文件 '{full}' 在 git 克隆 '{clone}' 里面。真实端点和凭据只能放在任何克隆之外，免得被提交。");
        }

        SettingsFileContent? file;
        try
        {
            file = JsonSerializer.Deserialize<SettingsFileContent>(File.ReadAllText(full), Options);
        }
        catch (JsonException e)
        {
            throw Refused($"设置文件 '{full}' 不是有效的 JSON：{e.Message}");
        }

        var missing = new List<string>();
        if (file?.Url is not { IsAbsoluteUri: true } url || url.Scheme is not ("http" or "https"))
        {
            missing.Add("url（http 或 https 的绝对地址）");
        }

        if (string.IsNullOrWhiteSpace(file?.CallApiKey))
        {
            missing.Add("callApiKey");
        }

        if (string.IsNullOrWhiteSpace(file?.VehicleKey))
        {
            missing.Add("vehicleKey");
        }

        if (file?.MapId is null or <= 0)
        {
            missing.Add("mapId");
        }

        if (file?.VehicleStation is null or <= 0)
        {
            missing.Add("vehicleStation");
        }

        if (file?.ReachableStation is null or <= 0)
        {
            missing.Add("reachableStation");
        }

        if (string.IsNullOrWhiteSpace(file?.Username) != string.IsNullOrWhiteSpace(file?.Password))
        {
            missing.Add("username 与 password（要么都给，要么都不给）");
        }

        if (file?.MapsSharingAStationId is { Length: not 2 } || (file?.MapsSharingAStationId is null) != (file?.SharedStationId is null))
        {
            missing.Add("mapsSharingAStationId（两个地图号）与 sharedStationId（要么都给，要么都不给）");
        }

        if (missing.Count > 0)
        {
            throw Refused($"设置文件 '{full}' 缺少或写错了：{string.Join("；", missing)}。");
        }

        var facts = new RiotTargetFacts(
            file!.VehicleKey!,
            file.MapId!.Value,
            file.VehicleStation!.Value,
            file.ReachableStation!.Value,
            file.SecondReachableStation ?? file.ReachableStation!.Value,
            file.UnreachableStation,
            file.ChargingStation,
            file.MapsSharingAStationId is [var first, var second] ? (first, second) : null,
            file.SharedStationId,
            file.Username,
            file.Password);
        return new RealRiotSettings(file.Url!, file.CallApiKey!, file.Username, file.Password, facts, full);
    }

    /// <summary>The nearest folder at or above the file's that holds a <c>.git</c> folder or file; null when there is none.</summary>
    public static string? EnclosingGitClone(string file)
    {
        for (var directory = new FileInfo(file).Directory; directory is not null; directory = directory.Parent)
        {
            var git = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    private static RealRiotRefusedException Refused(string reason) =>
        new($"不对真实 RIoT 跑契约测试：{reason}（设置文件由 {RiotTarget.RealSettingsVariable} 指定。）");

    private sealed record SettingsFileContent(
        Uri? Url,
        string? CallApiKey,
        string? Username,
        string? Password,
        string? VehicleKey,
        int? MapId,
        int? VehicleStation,
        int? ReachableStation,
        int? SecondReachableStation,
        int? UnreachableStation,
        int? ChargingStation,
        int[]? MapsSharingAStationId,
        int? SharedStationId);
}

public sealed class RealRiotRefusedException(string message) : Exception(message);
