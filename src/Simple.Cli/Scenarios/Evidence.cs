using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Simple.Logging;

namespace Simple.Cli.Scenarios;

/// <summary>
/// One run's evidence directory, outside the clone: its name carries the
/// time, the scenario and the run id. The timeline is written as the run
/// goes; the event record export, the program logs and result.json at the
/// end; MANIFEST.sha256 last, over every file. A file that cannot be
/// collected is listed in result.json and never changes the verdict.
/// </summary>
public sealed class Evidence : IDisposable
{
    public const string Timeline = "timeline.jsonl";
    public const string EventRecord = "events.jsonl";
    public const string FinalStatus = "status.json";
    public const string Result = "result.json";
    public const string Manifest = "MANIFEST.sha256";
    public const string Logs = "logs";
    public const string ScenarioCopy = "scenario.yaml";

    private static readonly JsonSerializerOptions JsonLine = new(Outcome.JsonOptions) { WriteIndented = false };

    private readonly StreamWriter timeline;
    private readonly Lock gate = new();

    private Evidence(string directory, string runId)
    {
        Directory = directory;
        RunId = runId;
        System.IO.Directory.CreateDirectory(directory);
        timeline = new StreamWriter(new FileStream(System.IO.Path.Combine(directory, Timeline), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new System.Text.UTF8Encoding(false))
        {
            AutoFlush = true,
        };
    }

    public string Directory { get; }

    public string RunId { get; }

    /// <summary>What could not be collected, file by file.</summary>
    public List<string> Missing { get; } = [];

    /// <summary>Creates <c>&lt;root&gt;/&lt;yyyyMMdd-HHmmss&gt;-&lt;scenario&gt;-&lt;run id&gt;</c>.</summary>
    public static Evidence Create(string root, string scenario, DateTimeOffset startedAt)
    {
        var runId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var name = $"{startedAt.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{scenario.Replace('/', '_')}-{runId}";
        return new Evidence(System.IO.Path.Combine(root, name), runId);
    }

    /// <summary>Appends one line to the timeline: when, what, and its details.</summary>
    public void Write(string kind, object? details = null)
    {
        var line = new JsonObject
        {
            ["time"] = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            ["kind"] = kind,
        };
        if (details is not null && JsonSerializer.SerializeToNode(details, JsonLine) is JsonObject fields)
        {
            foreach (var (key, value) in fields)
            {
                line[key] = value?.DeepClone();
            }
        }

        lock (gate)
        {
            timeline.WriteLine(line.ToJsonString(JsonLine));
        }
    }

    public void WriteJson(string file, object? value) =>
        Collect(file, () => File.WriteAllText(System.IO.Path.Combine(Directory, file), JsonSerializer.Serialize(value, Outcome.JsonOptions)));

    public void WriteLines(string file, IEnumerable<string> lines) =>
        Collect(file, () => File.WriteAllLines(System.IO.Path.Combine(Directory, file), lines));

    public void CopyFile(string source, string file) =>
        Collect(file, () =>
        {
            var target = System.IO.Path.Combine(Directory, file);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            using var from = ProgramLog.OpenRead(source);
            using var to = File.Create(target);
            from.BaseStream.CopyTo(to);
        });

    /// <summary>Copies a directory tree (the instance's logs), every file shared-read so a running process keeps writing.</summary>
    public void CopyDirectory(string source, string under)
    {
        if (!System.IO.Directory.Exists(source))
        {
            Missing.Add($"{under}: {source} does not exist");
            return;
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            CopyFile(file, System.IO.Path.Combine(under, System.IO.Path.GetRelativePath(source, file)));
        }
    }

    /// <summary>Writes MANIFEST.sha256 over every other file: "&lt;sha256&gt;  &lt;path&gt;", paths with '/'.</summary>
    public void Seal()
    {
        lock (gate)
        {
            timeline.Flush();
        }

        var lines = System.IO.Directory.EnumerateFiles(Directory, "*", SearchOption.AllDirectories)
            .Select(f => System.IO.Path.GetRelativePath(Directory, f).Replace('\\', '/'))
            .Where(f => f != Manifest)
            .Order(StringComparer.Ordinal)
            .Select(f =>
            {
                using var stream = new FileStream(System.IO.Path.Combine(Directory, f), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return $"{Convert.ToHexStringLower(SHA256.HashData(stream))}  {f}";
            })
            .ToList();
        File.WriteAllLines(System.IO.Path.Combine(Directory, Manifest), lines);
    }

    public void Dispose() => timeline.Dispose();

    private void Collect(string file, Action collect)
    {
        try
        {
            collect();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Missing.Add($"{file}: {e.Message}");
        }
    }
}
