namespace Simple.Protocol;

/// <summary>
/// The protocol's major version travels on the connection (ADR 0026), not in
/// each message: the terminal names it in a request header, and the server
/// refuses a major it does not serve with <c>FAILED_PRECONDITION</c>.
/// </summary>
public static class ProtocolVersion
{
    /// <summary>The major this build speaks: the messages in <c>simple.protocol.v1</c>.</summary>
    public const int Major = 1;

    /// <summary>Request header carrying the terminal's major, for example <c>1</c>.</summary>
    public const string MajorHeader = "simple-protocol-major";

    /// <summary>Trailer of a refusal: the majors the server serves, comma-separated, for example <c>1,2</c>.</summary>
    public const string SupportedMajorsTrailer = "simple-supported-majors";
}
