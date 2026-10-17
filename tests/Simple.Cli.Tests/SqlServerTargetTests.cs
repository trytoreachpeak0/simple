using Simple.Cli.Processes;

namespace Simple.Cli.Tests;

public sealed class SqlServerTargetTests
{
    [Theory]
    [InlineData(SqlServerTarget.LocalDb)]
    [InlineData(@"Server=.\SQLEXPRESS;Integrated Security=true;TrustServerCertificate=true")]
    [InlineData(@"Data Source=(local)\SQLEXPRESS;Integrated Security=true")]
    [InlineData(@"Server=tcp:localhost\SQLEXPRESS;Integrated Security=true")]
    [InlineData(@"Server=127.0.0.1\SQLEXPRESS;Integrated Security=true")]
    public void LocalDbAndNamedLocalInstancesAreAllowed(string connectionString)
    {
        Assert.Null(SqlServerTarget.Refusal(connectionString));
    }

    [Theory]
    [InlineData("Server=factory01;Integrated Security=true")]
    [InlineData(@"Server=factory01\SQLEXPRESS;Integrated Security=true")]
    [InlineData(@"Server=10.0.0.5\SQLEXPRESS;Integrated Security=true")]
    [InlineData("Server=.;Integrated Security=true")]
    [InlineData("Server=localhost;Integrated Security=true")]
    [InlineData("Server=(local);Integrated Security=true")]
    [InlineData("Server=tcp:127.0.0.1,1433;Integrated Security=true")]
    // With a port SqlClient ignores the instance name: on a development
    // machine 1433 is usually the default instance.
    [InlineData(@"Server=tcp:localhost\SQLEXPRESS,1433;Integrated Security=true")]
    [InlineData(@"Server=.\SQLEXPRESS,1433;Integrated Security=true")]
    [InlineData(@"Server=.\MSSQLSERVER;Integrated Security=true")]
    [InlineData(@"Server=localhost\mssqlserver;Integrated Security=true")]
    [InlineData(@"Server=.\SQLEXPRESS;Failover Partner=factory01;Integrated Security=true")]
    // A SQL login would be written to the state files in clear text.
    [InlineData(@"Server=.\SQLEXPRESS;User ID=sa;Password=not-a-real-one")]
    [InlineData(@"Server=(localdb)\MSSQLLocalDB;UID=simple;PWD=not-a-real-one")]
    [InlineData(@"Server=.\SQLEXPRESS;Integrated Security=true;Password=not-a-real-one")]
    [InlineData(@"Server=(localdb)\MSSQLLocalDB;Database=Simple_dev;Integrated Security=true")]
    [InlineData("not a connection string")]
    public void EverythingElseIsRefused(string connectionString)
    {
        Assert.NotNull(SqlServerTarget.Refusal(connectionString));
    }

    [Theory]
    [InlineData(true, "MSSQLLocalDB", "LAB-WIN-01")]
    [InlineData(false, "SQLEXPRESS", "LAB-WIN-01")]
    [InlineData(false, "sqlexpress", "lab-win-01")]
    public void AServerThatSaysItIsLocalDbOrANamedInstanceHereIsAccepted(bool isLocalDb, string instanceName, string machineName)
    {
        Assert.Null(SqlServerTarget.Judge(isLocalDb, instanceName, machineName, "LAB-WIN-01", "x"));
    }

    [Theory]
    [InlineData(null, "LAB-WIN-01")]
    [InlineData("MSSQLSERVER", "LAB-WIN-01")]
    [InlineData("SQLEXPRESS", "FACTORY01")]
    [InlineData("SQLEXPRESS", null)]
    public void ADefaultInstanceOrAnotherMachineIsRefusedWhateverTheConnectionStringSaid(string? instanceName, string? machineName)
    {
        Assert.NotNull(SqlServerTarget.Judge(false, instanceName, machineName, "LAB-WIN-01", @".\SQLEXPRESS"));
    }

    [Fact]
    public async Task TheTestSqlServerPassesTheCheckAtRunTime()
    {
        // LocalDB here, .\SQLEXPRESS on CI: both must be accepted by asking the server itself.
        Assert.Null(await SqlServerTarget.VerifyAsync(CliRunner.SqlServer, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TheDatabaseIsNamedAfterTheInstance()
    {
        Assert.Equal("Simple_wt25", SqlServerTarget.DatabaseName("wt25"));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("", "\"\"")]
    [InlineData(@"Server=(localdb)\MSSQLLocalDB;Integrated Security=true", @"""Server=(localdb)\MSSQLLocalDB;Integrated Security=true""")]
    [InlineData(@"a\""b", @"""a\\\""b""")]
    [InlineData(@"ends with\", @"""ends with\\""")]
    public void ArgumentsAreQuotedForTheChildsCommandLine(string argument, string expected)
    {
        Assert.Equal(expected, CommandLine.Quote(argument));
    }
}
