using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Simple.ProtocolProbe.Server;

/// <summary>
/// The server's self-generated certificate (ADR 0025). It is created on first
/// start as <c>server.pfx</c> in the data directory, outside the repository,
/// and never enters the Windows certificate store. The PFX has no password:
/// there is no secret besides the file itself, so nothing can leak into logs.
///
/// <c>--key-load machine</c> imports the key into the machine key store instead
/// of the user's. The probe uses it when it is run over SSH: an SSH public-key
/// logon has no DPAPI master key, so the default import fails ("Access is
/// denied"). In-memory keys (<c>--key-load ephemeral</c>) do not work either:
/// SChannel refuses them ("the platform does not support ephemeral keys").
/// The imported key container is temporary and is removed when the process
/// exits normally; its name is logged so that a killed process can be cleaned up.
/// </summary>
internal static class ServerCertificate
{
    public static X509Certificate2 LoadOrCreate(string dataDirectory, string keyAlgorithm, string keyLoad)
    {
        var path = Path.Combine(dataDirectory, "server.pfx");
        if (!File.Exists(path))
        {
            File.WriteAllBytes(path, Create(keyAlgorithm));
        }

        var flags = keyLoad switch
        {
            "ephemeral" => X509KeyStorageFlags.EphemeralKeySet,
            "machine" => X509KeyStorageFlags.MachineKeySet,
            _ => X509KeyStorageFlags.DefaultKeySet,
        };
        return X509CertificateLoader.LoadPkcs12FromFile(path, password: null, flags);
    }

    /// <summary>Where the private key was imported: CNG container name and store.</summary>
    public static string KeyContainer(X509Certificate2 certificate)
    {
        using var ecdsa = certificate.GetECDsaPrivateKey();
        using var rsa = ecdsa is null ? certificate.GetRSAPrivateKey() : null;
        var cng = (ecdsa as ECDsaCng)?.Key ?? (rsa as RSACng)?.Key;
        return cng is null ? "unknown" : $"{cng.KeyName ?? "(ephemeral)"} machine={cng.IsMachineKey} provider={cng.Provider?.Provider}";
    }

    /// <summary>Hex SHA-256 of the certificate's SubjectPublicKeyInfo: what the client pins.</summary>
    public static string Pin(X509Certificate2 certificate) =>
        Convert.ToHexStringLower(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));

    private static byte[] Create(string keyAlgorithm)
    {
        var subject = new X500DistinguishedName("CN=simple-probe-server");
        CertificateRequest request;
        AsymmetricAlgorithm key;
        if (keyAlgorithm == "rsa")
        {
            var rsa = RSA.Create(2048);
            key = rsa;
            request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        else
        {
            var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            key = ecdsa;
            request = new CertificateRequest(subject, ecdsa, HashAlgorithmName.SHA256);
        }

        using (key)
        {
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                keyAlgorithm == "rsa"
                    ? X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment
                    : X509KeyUsageFlags.DigitalSignature,
                true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.1")], false));
            using var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
            return certificate.Export(X509ContentType.Pkcs12);
        }
    }
}
