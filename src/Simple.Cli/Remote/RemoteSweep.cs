namespace Simple.Cli.Remote;

/// <summary>
/// What the remote side clears before heavy work, while no other heavy work
/// runs there: test databases and server keys that runs ended before their
/// tests could clean up left behind (a cut connection ends a run together
/// with its tests). Only names that match the patterns are touched.
/// </summary>
/// <param name="SqlServer">Where the tests run there make their databases (their SIMPLE_TEST_SQLSERVER); null clears no databases.</param>
/// <param name="KeyStore">The key store the tests run there keep their servers' keys in.</param>
/// <param name="Databases">A .NET regular expression the whole name of a leftover database matches, case-sensitive.</param>
/// <param name="Keys">A .NET regular expression the whole name of a leftover key matches, case-sensitive.</param>
public sealed record RemoteSweep(string? SqlServer, ServerKeyStore KeyStore, string Databases, string Keys)
{
    /// <summary>
    /// The database of a test instance, Simple_test_ and 12 lowercase hex
    /// digits (Simple.Server.Tests' ServerInstance, Simple.Cli.Tests'
    /// CliRunner). A worktree's instance is named after its folder and never
    /// looks like this.
    /// </summary>
    public const string TestDatabases = @"\ASimple_test_[0-9a-f]{12}\z";

    /// <summary>
    /// The key of a test instance's server, Simple-Server-test_ and 12 hex
    /// digits, and the key of the onboard terminal tests' fake server,
    /// Simple-Test- and a GUID's 32 hex digits.
    /// </summary>
    public const string TestKeys = @"\ASimple-(Server-test_[0-9a-f]{12}|Test-[0-9a-f]{32})\z";

    public static RemoteSweep ForTests(string? sqlServer, ServerKeyStore keyStore) => new(sqlServer, keyStore, TestDatabases, TestKeys);
}
