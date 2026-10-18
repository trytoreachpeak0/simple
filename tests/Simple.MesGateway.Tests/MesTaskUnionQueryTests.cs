using System.Security.Cryptography;
using Simple.MesGateway.Oracle;

namespace Simple.MesGateway.Tests;

/// <summary>
/// The gateway runs only the query the customer approved: the previous
/// line's MES_TASK_UNION, byte for byte, pinned by its SHA-256.
/// </summary>
public sealed class MesTaskUnionQueryTests
{
    /// <summary>The digest the previous line's MesIngest pins (CanonicalMesTaskUnionQuery.ExpectedSha256, 8005-mes-ingest@bb397e7).</summary>
    private const string ApprovedSha256 = "793452f171ce70520b187fc6411c771e731b3307906215422909d1814687847c";

    [Fact]
    public void TheCompiledInQueryIsTheApprovedOne()
    {
        Assert.Equal(ApprovedSha256, MesTaskUnionQuery.ExpectedSha256);
        Assert.Equal(ApprovedSha256, Sha256(MesTaskUnionQuery.Load()));
    }

    /// <summary>The file in the repository is what is compiled in, with the CRLF line endings the digest was taken over.</summary>
    [Fact]
    public void TheQueryFileInTheRepositoryHasTheApprovedDigest()
    {
        var bytes = File.ReadAllBytes(Path.Combine(RepositoryRoot(), "src", "Simple.MesGateway", "Oracle", "mes-task-union.sql"));

        Assert.Equal(ApprovedSha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Assert.Equal(MesTaskUnionQuery.Load(), MesTaskUnionQuery.Verify(bytes));
    }

    /// <summary>One byte changed anywhere, a line ending included, and the query is refused.</summary>
    [Theory]
    [InlineData("first")]
    [InlineData("middle")]
    [InlineData("last")]
    [InlineData("line ending")]
    [InlineData("appended")]
    public void AQueryWithOneByteChangedIsRefused(string where)
    {
        var bytes = File.ReadAllBytes(Path.Combine(RepositoryRoot(), "src", "Simple.MesGateway", "Oracle", "mes-task-union.sql"));
        switch (where)
        {
            case "first":
                bytes[0] ^= 1;
                break;
            case "middle":
                bytes[bytes.Length / 2] ^= 1;
                break;
            case "last":
                bytes[^1] ^= 1;
                break;
            case "line ending":
                bytes = [.. bytes.AsSpan(0, Array.IndexOf(bytes, (byte)'\r')), .. bytes.AsSpan(Array.IndexOf(bytes, (byte)'\r') + 1)];
                break;
            default:
                bytes = [.. bytes, (byte)' '];
                break;
        }

        var refused = Assert.Throws<InvalidDataException>(() => MesTaskUnionQuery.Verify(bytes));
        Assert.Contains(ApprovedSha256, refused.Message, StringComparison.Ordinal);
        Assert.Contains("not the approved", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyQueryIsRefused() =>
        Assert.Throws<InvalidDataException>(() => MesTaskUnionQuery.Verify([]));

    /// <summary>What the query returns is what the gateway's rows carry: the seven agreed columns, in that order.</summary>
    [Fact]
    public void TheQueryReturnsTheSevenAgreedColumns()
    {
        var sql = MesTaskUnionQuery.Load();

        Assert.Contains("统一输出顺序：TASK_TYPE, SUBLOT, AREA, EQP, STEP, DATES, PACKAGE", sql, StringComparison.Ordinal);
        Assert.Equal(7, sql.Split("AS TASK_TYPE,").Length - 1);
        Assert.Equal(6, sql.Split("\r\n").Count(line => line == "UNION ALL"));
        Assert.DoesNotContain(";", sql.TrimEnd(), StringComparison.Ordinal);
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    internal static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "simple.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"No simple.slnx above {AppContext.BaseDirectory}.");
    }
}
