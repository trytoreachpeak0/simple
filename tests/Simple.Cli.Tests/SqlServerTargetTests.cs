using Simple.Cli.Processes;

namespace Simple.Cli.Tests;

public sealed class SqlServerTargetTests
{
    [Theory]
    [InlineData(SqlServerTarget.LocalDb)]
    [InlineData(@"Server=.\SQLEXPRESS;Integrated Security=true;TrustServerCertificate=true")]
    [InlineData(@"Data Source=(local)\SQLEXPRESS;Integrated Security=true")]
    [InlineData(@"Server=tcp:localhost\SQLEXPRESS,1433;Integrated Security=true")]
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
    [InlineData(@"Server=(localdb)\MSSQLLocalDB;Database=Simple_dev;Integrated Security=true")]
    [InlineData("not a connection string")]
    public void EverythingElseIsRefused(string connectionString)
    {
        Assert.NotNull(SqlServerTarget.Refusal(connectionString));
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
