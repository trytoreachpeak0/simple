using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Simple.Server.OnboardTerminals;

namespace Simple.Server.Listeners;

/// <summary>
/// The certificate of the console's HTTPS address (ADR 0046), which the
/// console PCs install once as a trusted root. It says it is no certificate
/// authority and proves only this server: <c>basicConstraints</c> critical
/// with <c>CA:false</c>, <c>keyUsage</c> <c>digitalSignature</c> only,
/// extended key usage serverAuth only, and the server's IP address and
/// machine name as its subject alternative names. RFC 5280 §4.2.1.9 forbids
/// a <c>CA:false</c> key to verify certificate signatures, and Chrome and Edge
/// apply that to trusted roots (RFC 5937), so installing it lets nobody
/// impersonate another site.
/// </summary>
/// <remarks>
/// Unlike the terminal certificate, which is made anew from the same key on
/// every start because terminals pin the key, browsers trust the very
/// certificate installed: it is made once and kept (in the database), and
/// only the private key lives in the Windows key store, persisted and named,
/// the way <see cref="ServerCertificate"/> keeps the terminal key and for
/// the same reason (SChannel on Windows Server 2016 refuses a key held only
/// in memory).
/// </remarks>
public static class ConsoleCertificate
{
    /// <summary>How long it lasts (ADR 0046): long enough not to be renewed in the plant.</summary>
    public const int ValidYears = 20;

    /// <summary>Makes the certificate for the persisted key, valid from now for <see cref="ValidYears"/>.</summary>
    [SupportedOSPlatform("windows")]
    public static X509Certificate2 Create(CngKey key, IPAddress address, string machineName, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(address);
        using var ecdsa = new ECDsaCng(key);
        var request = new CertificateRequest(new X500DistinguishedName($"CN={machineName}"), ecdsa, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(address);
        names.AddDnsName(machineName);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(now.AddDays(-1), now.AddYears(ValidYears));
    }

    /// <summary>
    /// The stored certificate joined to its persisted key; null when the key
    /// is gone or is not the certificate's (restored on another machine, deleted).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static X509Certificate2? Join(byte[] certificate, string keyName, bool machineKey)
    {
        if (!ServerCertificate.Exists(keyName, machineKey))
        {
            return null;
        }

        using var stored = X509CertificateLoader.LoadCertificate(certificate);
        using var key = ServerCertificate.OpenOrCreateKey(keyName, machineKey);
        using var ecdsa = new ECDsaCng(key);
        using var published = stored.GetECDsaPublicKey();
        if (published is null || !published.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(ecdsa.ExportSubjectPublicKeyInfo()))
        {
            return null;
        }

        return stored.CopyWithPrivateKey(ecdsa);
    }

    /// <summary>The IP addresses and DNS names it is for, in that order.</summary>
    public static IReadOnlyList<string> NamesOf(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var names = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().SingleOrDefault();
        return names is null
            ? []
            : [.. names.EnumerateIPAddresses().Select(a => a.ToString()), .. names.EnumerateDnsNames()];
    }

    /// <summary>The SHA-256 of the whole certificate, in lowercase hex: what an installed copy is checked against.</summary>
    public static string Sha256Of(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));
    }
}
