using Simple.Contracts.Tests.Targets;

namespace Simple.Contracts.Tests;

/// <summary>What the in-process fake leaves behind when a contract is done with it.</summary>
public sealed class FakeRiotTargetTests
{
    /// <summary>
    /// A file in the fake's log folder can still be held when the factory has
    /// returned; on simple01 that failed contracts that had passed (#80).
    /// </summary>
    [Fact]
    public async Task ALogFileStillHeldDoesNotFailTheContract()
    {
        var target = (FakeRiotTarget)await FakeRiotTarget.StartAsync();
        Directory.CreateDirectory(target.LogDirectory);
        var held = Path.Combine(target.LogDirectory, "held.log");
        using (new FileStream(held, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            await target.DisposeAsync();
        }

        // Left in %TEMP%, where it harms nothing.
        Assert.True(File.Exists(held));
    }
}
