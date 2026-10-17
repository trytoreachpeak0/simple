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

    private readonly TextWriter timeline;
    private readonly Lock gate = new();
    private bool timelineBroken;

    private Evidence(string directory, string runId)
        : this(directory, runId, OpenTimeline(directory))
    {
    }

    /// <summary>Evidence written through <paramref name="timeline"/>; tests give one that fails.</summary>
    internal Evidence(string directory, string runId, TextWriter timeline)
    {
        Directory = directory;
        RunId = runId;
        this.timeline = timeline;
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

    /// <summary>
    /// Appends one line to the timeline: when, what, and its details. Like
    /// every piece of evidence it never fails the run: a line that cannot be
    /// written (a full disk) is listed in <see cref="Missing"/>, once.
    /// </summary>
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
            try
            {
                timeline.WriteLine(line.ToJsonString(JsonLine));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                if (!timelineBroken)
                {
                    timelineBroken = true;
                    Missing.Add($"{Timeline}: lines from '{kind}' on could not be written: {e.Message}");
                }
            }
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
            try
            {
                timeline.Flush();
            }
            catch (IOException)
            {
                // Already listed as missing when its line failed.
            }
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

    public void Dispose()
    {
        try
        {
            timeline.Dispose();
        }
        catch (IOException)
        {
            // The last lines are lost; the run's outcome stands.
        }
    }

    private static StreamWriter OpenTimeline(string directory)
    {
        System.IO.Directory.CreateDirectory(directory);
        return new StreamWriter(new FileStream(System.IO.Path.Combine(directory, Timeline), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new System.Text.UTF8Encoding(false))
        {
            AutoFlush = true,
        };
    }

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
