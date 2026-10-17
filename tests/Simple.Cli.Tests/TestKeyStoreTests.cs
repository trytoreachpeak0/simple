namespace Simple.Cli.Tests;

public sealed class TestKeyStoreTests
{
    [Theory]
    [InlineData(null, ServerKeyStore.User)]
    [InlineData("", ServerKeyStore.User)]
    [InlineData("user", ServerKeyStore.User)]
    [InlineData("Machine", ServerKeyStore.Machine)]
    public void UnsetOrUserIsTheUsersStore(string? value, ServerKeyStore expected)
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
