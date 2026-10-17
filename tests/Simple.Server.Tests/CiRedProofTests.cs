namespace Simple.Server.Tests;

// Throwaway (#28): proves CI turns red when a test fails. Reverted before review.
public sealed class CiRedProofTests
{
    [Fact]
    public void FailsOnPurpose() => Assert.Fail("Deliberate failure to prove CI turns red (#28).");
}
