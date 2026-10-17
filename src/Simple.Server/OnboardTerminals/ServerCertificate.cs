using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Simple.Protocol;

namespace Simple.Server.OnboardTerminals;

/// <summary>
/// The certificate the server presents to onboard terminals (ADR 0025). Its
/// private key is a named, persisted CNG key (ECDSA P-256) in the key store
/// of the account the server runs as, or in the machine's: created on the
/// server's first start, opened by name on every later one. The certificate
/// itself is made anew from that key on every start; terminals pin the
/// public key (<see cref="CertificatePin"/>), so the pin stays the same as
/// long as the key does.
/// </summary>
/// <remarks>
/// Why a named key and not a PFX file (#18): SChannel on Windows Server 2016
/// refuses a private key that only lives in memory ("the platform does not
/// support ephemeral keys"), and importing a PFX always creates a temporary
/// key container, which a killed process leaves behind. Binding a persisted
/// key to the certificate (<see cref="ECDsaCertificateExtensions.CopyWithPrivateKey"/>)
/// only records the key's name and provider on the certificate; SChannel
/// opens the key by that name, so nothing is imported and nothing temporary
/// is ever created. The key may be exported, encrypted, so that it can be
/// backed up: losing it means configuring and confirming every terminal again.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class ServerCertificate
{
    private static readonly CngProvider Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider;

    /// <summary>
    /// Opens the named key, creating it first if it does not exist, and makes a
    /// certificate for it. <c>Created</c> tells that the key is new, and with it
    /// the pin: terminals pinned to an earlier key refuse this server.
    /// </summary>
    public static (X509Certificate2 Certificate, bool Created) LoadOrCreate(string keyName, bool machineKey)
    {
        var created = !Exists(keyName, machineKey);
        using var key = OpenOrCreateKey(keyName, machineKey);
        using var ecdsa = new ECDsaCng(key);
        var request = new CertificateRequest(new X500DistinguishedName("CN=simple-server"), ecdsa, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        // Terminals look at the public key only, so the validity period
        // matters to nothing; the certificate is made anew on every start.
        var now = DateTimeOffset.UtcNow;
        return (request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10)), created);
    }

    public static bool Exists(string keyName, bool machineKey) =>
        CngKey.Exists(keyName, Provider, machineKey ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None);

    /// <summary>Deletes the named key, if it exists. Every terminal pinned to it must then be configured again.</summary>
    /// <returns>Whether there was a key to delete.</returns>
    public static bool Delete(string keyName, bool machineKey)
    {
        if (!Exists(keyName, machineKey))
        {
            return false;
        }

        using var key = CngKey.Open(keyName, Provider, machineKey ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None);
        key.Delete();
        return true;
    }

    private static CngKey OpenOrCreateKey(string keyName, bool machineKey)
    {
        if (Exists(keyName, machineKey))
        {
            return CngKey.Open(keyName, Provider, machineKey ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None);
        }

        try
        {
            return CngKey.Create(CngAlgorithm.ECDsaP256, keyName, new CngKeyCreationParameters
            {
                Provider = Provider,
                KeyCreationOptions = machineKey ? CngKeyCreationOptions.MachineKey : CngKeyCreationOptions.None,
                ExportPolicy = CngExportPolicies.AllowExport,
                KeyUsage = CngKeyUsages.Signing,
            });
        }
        catch (CryptographicException) when (Exists(keyName, machineKey))
        {
            // Another server of the same instance created it a moment ago.
            return CngKey.Open(keyName, Provider, machineKey ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None);
        }
    }
}
