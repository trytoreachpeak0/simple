namespace Simple.Server.Tests;

public sealed class TestKeyStoreTests
{
    [Theory]
    [InlineData(null, "User")]
    [InlineData("", "User")]
    [InlineData("user", "User")]
    [InlineData("Machine", "Machine")]
    public void UnsetOrUserIsTheUsersStore(string? value, string expected)
    {
        Assert.Equal(expected, TestKeyStore.From(value));
    }

    [Theory]
    [InlineData("Both")]
    [InlineData("1")]
    public void AnyOtherValueFailsTheTests(string value)
    {
        Assert.Contains(value, Assert.Throws<InvalidOperationException>(() => TestKeyStore.From(value)).Message, StringComparison.Ordinal);
    }
}
