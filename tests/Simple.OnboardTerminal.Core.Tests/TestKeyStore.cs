namespace Simple.OnboardTerminal.Core.Tests;

/// <summary>
/// Where the fake server keeps its key: <c>SIMPLE_TEST_SERVER_KEY_STORE</c>,
/// <c>User</c> (the default, on development machines and CI) or <c>Machine</c>.
/// simple remote sets Machine: an SSH key login cannot use the user's store
/// (no DPAPI). Any other value fails the tests instead of quietly using the
/// user's store.
/// </summary>
public static class TestKeyStore
{
    public const string Variable = "SIMPLE_TEST_SERVER_KEY_STORE";

    /// <summary>User or Machine.</summary>
    public static string Name { get; } = From(Environment.GetEnvironmentVariable(Variable));

    public static bool IsMachine => Name == "Machine";

    public static string From(string? value) => value switch
    {
        null or "" => "User",
        _ when string.Equals(value, "User", StringComparison.OrdinalIgnoreCase) => "User",
        _ when string.Equals(value, "Machine", StringComparison.OrdinalIgnoreCase) => "Machine",
        _ => throw new InvalidOperationException($"{Variable} is '{value}': it must be User or Machine, or unset."),
    };
}
