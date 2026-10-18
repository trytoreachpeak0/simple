using System.Security.Cryptography;
using System.Text;

namespace Simple.MesGateway.Oracle;

/// <summary>
/// The one query the gateway runs on the MES: the previous line's
/// MES_TASK_UNION, which the customer's IT approved, carried over byte for
/// byte (<c>Oracle/mes-task-union.sql</c>, compiled in). Its SHA-256 is fixed
/// here, so a query that differs by one byte is refused, not run.
/// </summary>
/// <remarks>
/// The text and the digest are those of 8005-mes-ingest's
/// <c>queries/mes-task-union/query.sql</c> and
/// <c>CanonicalMesTaskUnionQuery.ExpectedSha256</c> at bb397e7 (2026-10-16,
/// seven task types), taken over CRLF line endings as there; the folder's
/// <c>.gitattributes</c> keeps them on every checkout. The query selects
/// only; whether a demand comes of a row is the server's business.
/// </remarks>
public static class MesTaskUnionQuery
{
    public const string Id = "MES_TASK_UNION";

    public const string ExpectedSha256 = "793452f171ce70520b187fc6411c771e731b3307906215422909d1814687847c";

    private const string ResourceName = "Simple.MesGateway.Oracle.mes-task-union.sql";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly Lazy<string> Compiled = new(() =>
    {
        using var stream = typeof(MesTaskUnionQuery).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException($"The approved MES query {Id} is not compiled into the gateway.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return Verify(bytes.ToArray());
    });

    /// <summary>The compiled-in query, checked against <see cref="ExpectedSha256"/>; throws <see cref="InvalidDataException"/> when it is not the approved one.</summary>
    public static string Load() => Compiled.Value;

    /// <summary>The query text when <paramref name="bytes"/> are exactly the approved query; otherwise throws <see cref="InvalidDataException"/>.</summary>
    public static string Verify(ReadOnlySpan<byte> bytes)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(digest, ExpectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The MES query {Id} is not the approved one: its SHA-256 is {digest}, the approved query's is {ExpectedSha256}. The gateway runs only the query the customer's IT approved, unchanged.");
        }

        return StrictUtf8.GetString(bytes);
    }
}
