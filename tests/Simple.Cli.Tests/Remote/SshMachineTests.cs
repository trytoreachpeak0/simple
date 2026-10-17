using Simple.Cli.Remote;

namespace Simple.Cli.Tests.Remote;

/// <summary>
/// SshMachine itself, with a stand-in for ssh that does what sshd with
/// DefaultShell pwsh does with the command: runs it under <c>pwsh -c</c>,
/// with this end's input as its input.
/// </summary>
public sealed class SshMachineTests : IDisposable
{
    private const string FakeSsh =
        """
        #Requires -Version 7
        # Stand-in for ssh: says which options it got, then runs the last argument as sshd would.
        [Console]::Error.WriteLine('ssh options: ' + ($args[0..($args.Count - 3)] -join ' '))
        $start = [Diagnostics.ProcessStartInfo]::new('pwsh')
        foreach ($argument in '-NoProfile', '-NonInteractive', '-c', $args[-1]) { $start.ArgumentList.Add($argument) }
        $start.UseShellExecute = $false
        $session = [Diagnostics.Process]::Start($start)
        $session.WaitForExit()
        exit $session.ExitCode
        """;

    private readonly string directory = Directory.CreateTempSubdirectory("simple-ssh-test-").FullName;

    [Fact]
    public async Task ItTellsTheRemoteSideItIsStillThere()
    {
        var fake = Path.Combine(directory, "fake-ssh.ps1");
        File.WriteAllText(fake, FakeSsh);
        var machine = new SshMachine("remote-test", ["pwsh", "-NoProfile", "-File", fake]);
        var output = new List<string>();
        var errors = new List<string>();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(60));

        // The remote script reads its input as the agent does: the first line must be a heartbeat.
        await machine.RunAsync(
            "$reader = [IO.StreamReader]::new([Console]::OpenStandardInput()); 'first line: ' + $reader.ReadLine()",
            line => { lock (output) { output.Add(line); } },
            line => { lock (errors) { errors.Add(line); } },
            limit.Token);

        Assert.Contains($"first line: {LineProcess.HeartbeatLine}", output);
        var options = Assert.Single(errors, e => e.StartsWith("ssh options: ", StringComparison.Ordinal));
        Assert.Contains("ServerAliveInterval=10", options, StringComparison.Ordinal);
        Assert.Contains("ServerAliveCountMax=3", options, StringComparison.Ordinal);
        Assert.Contains("BatchMode=yes", options, StringComparison.Ordinal);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
