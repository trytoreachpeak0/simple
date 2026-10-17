using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Serilog;

namespace Simple.ProtocolProbe.Client;

/// <summary>
/// The terminal's own key (ADR 0025): generated on first start in a named,
/// non-exportable CNG key (software provider, or the TPM through the Microsoft
/// Platform Crypto Provider), with a self-signed certificate in
/// CurrentUser\My. <c>identity.json</c> in the data directory only records
/// where they are and which vehicle the server confirmed; it holds no secret.
///
/// <c>--key-store machine</c> puts the key in the machine key store and the
/// certificate in LocalMachine\My instead (needs an administrator). The probe
/// uses it when it is run over SSH: an SSH public-key logon has no DPAPI master
/// key, so a CurrentUser key cannot be created there ("Access is denied").
/// </summary>
internal sealed class TerminalIdentity
{
    public const string TpmProvider = "Microsoft Platform Crypto Provider";
    private const string Subject = "CN=simple-probe-terminal";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string path;

    private TerminalIdentity(string path, IdentityFile file, X509Certificate2 certificate)
    {
        this.path = path;
        File = file;
        Certificate = certificate;
    }

    public IdentityFile File { get; }

    public X509Certificate2 Certificate { get; }

    /// <summary>Hex SHA-256 of the SubjectPublicKeyInfo; the server knows the terminal by it.</summary>
    public string Key => Convert.ToHexStringLower(SHA256.HashData(Certificate.PublicKey.ExportSubjectPublicKeyInfo()));

    public static TerminalIdentity LoadOrCreate(string dataDirectory, string provider, string algorithm, bool machine)
    {
        var path = Path.Combine(dataDirectory, "identity.json");
        if (!System.IO.File.Exists(path))
        {
            Create(path, provider, algorithm, machine);
        }

        var file = JsonSerializer.Deserialize<IdentityFile>(System.IO.File.ReadAllText(path), Json)!;
        using var store = new X509Store(StoreName.My, file.Location);
        store.Open(OpenFlags.ReadOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, file.Thumbprint, validOnly: false);
        if (found.Count == 0)
        {
            throw new InvalidOperationException($"identity.json names certificate {file.Thumbprint}, which is not in {file.Location}\\My; run with --reset");
        }

        return new TerminalIdentity(path, file, found[0]);
    }

    public static void Reset(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "identity.json");
        if (!System.IO.File.Exists(path))
        {
            Log.Information("RESET nothing to do: {Path} does not exist", path);
            return;
        }

        var file = JsonSerializer.Deserialize<IdentityFile>(System.IO.File.ReadAllText(path), Json)!;
        using (var store = new X509Store(StoreName.My, file.Location))
        {
            store.Open(OpenFlags.ReadWrite);
            foreach (var certificate in store.Certificates.Find(X509FindType.FindByThumbprint, file.Thumbprint, validOnly: false))
            {
                store.Remove(certificate);
            }

            Log.Information("RESET certificate {Thumbprint} in {Location}\\My after removal: {Count}",
                file.Thumbprint, file.Location, store.Certificates.Find(X509FindType.FindByThumbprint, file.Thumbprint, validOnly: false).Count);
        }

        var provider = new CngProvider(file.Provider);
        var open = file.Machine ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None;
        if (CngKey.Exists(file.KeyName, provider, open))
        {
            using var key = CngKey.Open(file.KeyName, provider, open);
            key.Delete();
        }

        Log.Information("RESET key {KeyName} ({Provider}, machine={Machine}) exists after removal: {Exists}",
            file.KeyName, file.Provider, file.Machine, CngKey.Exists(file.KeyName, provider, open));
        System.IO.File.Delete(path);
        Log.Information("RESET {Path} exists after removal: {Exists}", path, System.IO.File.Exists(path));
    }

    public void SaveVehicle(string? vehicleId)
    {
        File.VehicleId = vehicleId;
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(File, Json));
    }

    public void Describe()
    {
        using var ecdsa = Certificate.GetECDsaPrivateKey();
        using var rsa = ecdsa is null ? Certificate.GetRSAPrivateKey() : null;
        var cng = (ecdsa as ECDsaCng)?.Key ?? (rsa as RSACng)?.Key;
        Log.Information("IDENTITY key={Key} thumbprint={Thumbprint} algorithm={Algorithm} provider={Provider} machineKey={Machine} exportPolicy={Export} store={Store}\\My keyName={KeyName} vehicle={Vehicle}",
            Key[..12], Certificate.Thumbprint, File.Algorithm, cng?.Provider?.Provider ?? "?", cng?.IsMachineKey.ToString() ?? "?",
            cng?.ExportPolicy.ToString() ?? "?", File.Location, File.KeyName, File.VehicleId ?? "-");
    }

    private static void Create(string path, string providerChoice, string algorithm, bool machine)
    {
        var provider = providerChoice == "tpm" ? new CngProvider(TpmProvider) : CngProvider.MicrosoftSoftwareKeyStorageProvider;
        var keyName = $"simple-probe-{Guid.NewGuid():N}";
        // The Platform Crypto Provider rejects explicit export-policy and usage
        // properties ("The parameter is incorrect"); TPM keys never leave the
        // TPM anyway. The software provider gets them set explicitly.
        var parameters = providerChoice == "tpm"
            ? new CngKeyCreationParameters { Provider = provider }
            : new CngKeyCreationParameters { Provider = provider, ExportPolicy = CngExportPolicies.None, KeyUsage = CngKeyUsages.AllUsages };
        if (machine)
        {
            parameters.KeyCreationOptions = CngKeyCreationOptions.MachineKey;
        }
        if (algorithm == "rsa")
        {
            parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None));
        }

        Log.Information("IDENTITY creating key {KeyName} in {Provider} ({Algorithm}, not exportable, machine={Machine})", keyName, provider.Provider, algorithm, machine);
        using var key = CngKey.Create(algorithm == "rsa" ? CngAlgorithm.Rsa : CngAlgorithm.ECDsaP256, keyName, parameters);
        var subject = new X500DistinguishedName(Subject);
        using AsymmetricAlgorithm signer = algorithm == "rsa" ? new RSACng(key) : new ECDsaCng(key);
        var request = signer is RSA rsa
            ? new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            : new CertificateRequest(subject, (ECDsa)signer, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        using (var store = new X509Store(StoreName.My, machine ? StoreLocation.LocalMachine : StoreLocation.CurrentUser))
        {
            store.Open(OpenFlags.ReadWrite);
            store.Add(certificate);
        }

        var file = new IdentityFile
        {
            KeyName = keyName,
            Provider = provider.Provider,
            Algorithm = algorithm,
            Thumbprint = certificate.Thumbprint,
            Machine = machine,
        };
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(file, Json));
    }
}

internal sealed class IdentityFile
{
    public required string KeyName { get; init; }
    public required string Provider { get; init; }
    public required string Algorithm { get; init; }
    public required string Thumbprint { get; init; }
    public bool Machine { get; init; }
    public string? VehicleId { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public StoreLocation Location => Machine ? StoreLocation.LocalMachine : StoreLocation.CurrentUser;
}
