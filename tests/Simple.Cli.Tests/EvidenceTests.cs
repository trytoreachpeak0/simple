using Simple.Cli.Scenarios;

namespace Simple.Cli.Tests;

/// <summary>Evidence never fails a run: what cannot be written is listed as missing, and the run goes on to stop its system.</summary>
public sealed class EvidenceTests
{
    [Fact]
    public void ATimelineThatCannotBeWrittenIsListedOnceAndNeverThrows()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"simple-evidence-{Guid.NewGuid():N}", "never-created");
        using var evidence = new Evidence(directory, "r", new FullDisk());

        evidence.Write("run-started", new { Scenario = "s" });
        evidence.Write("down", new { Stopped = true });
        evidence.WriteJson(Evidence.Result, new { Verdict = "holds" });

        Assert.Equal(2, evidence.Missing.Count);
        Assert.StartsWith($"{Evidence.Timeline}: lines from 'run-started' on could not be written", evidence.Missing[0], StringComparison.Ordinal);
        Assert.StartsWith($"{Evidence.Result}: ", evidence.Missing[1], StringComparison.Ordinal);
    }

    private sealed class FullDisk : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value) => throw new IOException("There is not enough space on the disk.");

        public override void WriteLine(string? value) => throw new IOException("There is not enough space on the disk.");
    }
}
