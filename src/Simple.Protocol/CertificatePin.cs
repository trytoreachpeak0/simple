using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Simple.Protocol;

/// <summary>
/// How an onboard terminal recognises its server (ADR 0025): the lowercase
/// hex SHA-256 of the server certificate's SubjectPublicKeyInfo. The pin
/// follows the key, not the certificate, so a certificate made anew from the
/// same key keeps it; host name, chain and validity are not looked at.
/// </summary>
public static class CertificatePin
{
    public static string Of(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexStringLower(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>Whether <paramref name="text"/> has the shape of a pin: 64 hex digits.</summary>
    public static bool IsWellFormed(string? text) =>
        text is { Length: 64 } && text.All(Uri.IsHexDigit);

    /// <summary>Compares two pins, ignoring case.</summary>
    public static bool Matches(string pinned, string presented) =>
        string.Equals(pinned, presented, StringComparison.OrdinalIgnoreCase);
}
