namespace Simple.Cli.Tests;

/// <summary>
/// Where the servers the tests bring up keep their keys: <c>SIMPLE_TEST_SERVER_KEY_STORE</c>,
/// <c>User</c> (the default, on development machines and CI) or <c>Machine</c>.
/// simple remote sets Machine: an SSH key login cannot use the user's store
/// (no DPAPI). Any other value fails the tests instead of quietly using the
/// user's store. Handed to <c>simple up</c> as <c>--server-key-store</c>.
/// </summary>
public static class TestKeyStore
{
    public const string Variable = "SIMPLE_TEST_SERVER_KEY_STORE";

    public static ServerKeyStore Store { get; } = From(Environment.GetEnvironmentVariable(Variable));

    /// <summary>The store the key must not be in.</summary>
    public static ServerKeyStore Other => Store == ServerKeyStore.User ? ServerKeyStore.Machine : ServerKeyStore.User;

    public static ServerKeyStore From(string? value) =>
        string.IsNullOrEmpty(value)
            ? ServerKeyStore.User
            : ServerKey.TryParse(value, out var store)
                ? store
                : throw new InvalidOperationException($"{Variable} is '{value}': it must be User or Machine, or unset.");
}
