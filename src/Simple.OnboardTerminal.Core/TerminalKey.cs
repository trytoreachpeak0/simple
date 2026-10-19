using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Simple.Protocol;

namespace Simple.OnboardTerminal.Core;

/// <summary>
/// The terminal's own key (ADR 0025): a named, persisted ECDSA P-256 CNG key
/// that never leaves this computer. Created on the terminal's first start,
/// in the TPM when the computer has one and the settings allow it
/// (Microsoft Platform Crypto Provider), otherwise in the software key store
/// (Microsoft Software Key Storage Provider) and not exportable; opened by
/// name on every later start. The certificate the terminal presents is made
/// anew from it on every start and only lives in memory; the server knows
/// the terminal by the key's pin (<see cref="CertificatePin"/>), which stays
/// the same as long as the key does.
/// </summary>
/// <remarks>
/// A key in the current user's store needs that user's DPAPI master key, which
/// a session without it (an SSH key logon) does not have: there, creating the
/// key fails, and the terminal says so (<see cref="TerminalKeyException"/>).
/// The terminal creates its key itself, run by the account logged in at the
/// vehicle; no script over SSH can do it in its place (ADR 0025). A key in the
/// machine's store does not need DPAPI, but only an administrator can create
/// one; tests and <c>simple remote</c> use it.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class TerminalKey : IDisposable
{
    /// <summary>The CNG provider of keys in the TPM.</summary>
    public const string TpmProviderName = "Microsoft Platform Crypto Provider";

    private TerminalKey(string name, CngProvider provider, bool machineKey, bool created, X509Certificate2 certificate)
    {
        Name = name;
        Provider = provider.Provider;
        MachineKey = machineKey;
        Created = created;
        Certificate = certificate;
        Pin = CertificatePin.Of(certificate);
    }

    public string Name { get; }

    /// <summary>The CNG provider the key is in: the TPM's or the software key store's.</summary>
    public string Provider { get; }

    public bool InTpm => Provider == TpmProviderName;

    public bool MachineKey { get; }

    /// <summary>Whether this start created the key: the server does not know it yet.</summary>
    public bool Created { get; }

    /// <summary>The certificate the terminal presents, made from the key on this start.</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>What the server knows the terminal by: the hex SHA-256 of the key's public half.</summary>
    public string Pin { get; }

    /// <summary>
    /// Opens the named key, from the TPM or the software key store, or creates
    /// it: in the TPM first when <paramref name="tryTpm"/> and the computer has
    /// one, otherwise in the software key store, not exportable.
    /// </summary>
    /// <param name="tpmProblem">Why the key was not created in the TPM although asked to; null otherwise.</param>
    /// <exception cref="TerminalKeyException">The key could not be opened or created; the message says why and what to do.</exception>
    public static TerminalKey OpenOrCreate(string name, bool machineKey, bool tryTpm, out string? tpmProblem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        tpmProblem = null;
        var open = machineKey ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None;
        try
        {
            foreach (var provider in Providers)
            {
                if (Exists(name, provider, open))
                {
                    using var existing = CngKey.Open(name, provider, open);
                    return new TerminalKey(name, provider, machineKey, created: false, MakeCertificate(existing));
                }
            }
        }
        catch (CryptographicException e)
        {
            throw new TerminalKeyException($"Cannot open the terminal's key {name} in the {Store(machineKey)} key store: {e.Message}", e);
        }

        if (tryTpm)
        {
            try
            {
                // The TPM's provider takes no export policy or key usage ("The
                // parameter is incorrect", #18); a TPM key never leaves it anyway.
                using var key = CngKey.Create(CngAlgorithm.ECDsaP256, name, new CngKeyCreationParameters
                {
                    Provider = new CngProvider(TpmProviderName),
                    KeyCreationOptions = machineKey ? CngKeyCreationOptions.MachineKey : CngKeyCreationOptions.None,
                });
                return new TerminalKey(name, key.Provider!, machineKey, created: true, MakeCertificate(key));
            }
            catch (CryptographicException e)
            {
                // No TPM, or one this account cannot use: the software store.
                tpmProblem = e.Message;
            }
        }

        try
        {
            using var key = CngKey.Create(CngAlgorithm.ECDsaP256, name, new CngKeyCreationParameters
            {
                Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
                KeyCreationOptions = machineKey ? CngKeyCreationOptions.MachineKey : CngKeyCreationOptions.None,
                ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.Signing,
            });
            return new TerminalKey(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, machineKey, created: true, MakeCertificate(key));
        }
        catch (CryptographicException e)
        {
            throw new TerminalKeyException(CreateFailed(name, machineKey, e.Message, Environment.GetEnvironmentVariable("SSH_CONNECTION") is { Length: > 0 }), e);
        }
    }

    /// <summary>Whether the named key exists in the TPM or the software key store.</summary>
    public static bool Exists(string name, bool machineKey) =>
        Providers.Any(p => Exists(name, p, machineKey ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None));

    /// <summary>Deletes the named key wherever it is. The server then no longer knows this terminal: it has to be confirmed again.</summary>
    /// <returns>Whether there was a key to delete.</returns>
    public static bool Delete(string name, bool machineKey)
    {
        var open = machineKey ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None;
        var deleted = false;
        foreach (var provider in Providers)
        {
            if (Exists(name, provider, open))
            {
                using var key = CngKey.Open(name, provider, open);
                key.Delete();
                deleted = true;
            }
        }

        return deleted;
    }

    /// <summary>What the terminal says when it cannot create its key, with what to do about it.</summary>
    public static string CreateFailed(string name, bool machineKey, string reason, bool overSsh) =>
        machineKey
            ? $"Cannot create the terminal's key {name} in the machine key store: {reason} Only an administrator can create a key there."
            : $"Cannot create the terminal's key {name} in the current user's key store: {reason} "
                + "The terminal creates its key itself, run by the account logged in at the vehicle: a session without that user's DPAPI master key, "
                + "such as an SSH key logon, cannot create it, and no script over SSH can do it in the terminal's place (ADR 0025)."
                + (overSsh ? " This process runs in an SSH session (SSH_CONNECTION is set)." : string.Empty);

    public void Dispose() => Certificate.Dispose();

    private static IEnumerable<CngProvider> Providers => [new CngProvider(TpmProviderName), CngProvider.MicrosoftSoftwareKeyStorageProvider];

    private static string Store(bool machineKey) => machineKey ? "machine" : "current user's";

    private static bool Exists(string name, CngProvider provider, CngKeyOpenOptions open)
    {
        try
        {
            return CngKey.Exists(name, provider, open);
        }
        catch (CryptographicException) when (provider.Provider == TpmProviderName)
        {
            // No TPM provider on this computer: no key in it either.
            return false;
        }
    }

    /// <summary>
    /// A self-signed client certificate bound by name to the persisted key:
    /// only the key's name and provider are recorded on it, and SChannel opens
    /// the key by that name. Nothing is imported, nothing is written to a
    /// certificate store, and no temporary key container is made (as the
    /// server's certificate, ADR 0025).
    /// </summary>
    private static X509Certificate2 MakeCertificate(CngKey key)
    {
        using var ecdsa = new ECDsaCng(key);
        var request = new CertificateRequest(new X500DistinguishedName("CN=simple-onboard-terminal"), ecdsa, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false));
        // The server looks at the public key only, so the validity period
        // matters to nothing; the certificate is made anew on every start.
        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
    }
}

/// <summary>The terminal's key could not be opened or created; the message says why and what to do.</summary>
public sealed class TerminalKeyException(string message, Exception inner) : Exception(message, inner);
