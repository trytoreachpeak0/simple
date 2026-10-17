using System.Security.Cryptography;

namespace Simple.Cli;

/// <summary>Where a local server keeps the key its terminal certificate is made from.</summary>
public enum ServerKeyStore
{
    /// <summary>The current user's key store: the default, and all a development machine needs.</summary>
    User,

    /// <summary>
    /// The machine's key store. simple remote up asks for it: over an SSH key
    /// login the user's store cannot be used (no DPAPI). Only an administrator
    /// can create or delete a key there.
    /// </summary>
    Machine,
}

/// <summary>
/// The persisted key a local server makes its terminal certificate from (see
/// Simple.Server's OnboardTerminals.ServerCertificate). Only down --reset and
/// the tests' clean-up delete it.
/// </summary>
public static class ServerKey
{
    private static readonly CngProvider Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider;

    /// <summary>The value the server's Simple:OnboardTerminals:ServerKeyStore takes, and the text of the option.</summary>
    public static bool TryParse(string? text, out ServerKeyStore store)
    {
        foreach (var candidate in Enum.GetValues<ServerKeyStore>())
        {
            if (string.Equals(text, candidate.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                store = candidate;
                return true;
            }
        }

        store = default;
        return false;
    }

    public static bool Exists(string name, ServerKeyStore store) => CngKey.Exists(name, Provider, OpenOptions(store));

    /// <returns>Whether there was a key to delete.</returns>
    /// <exception cref="CryptographicException">It could not be deleted, for example from the machine's store without being an administrator.</exception>
    public static bool Delete(string name, ServerKeyStore store)
    {
        if (!Exists(name, store))
        {
            return false;
        }

        using var key = CngKey.Open(name, Provider, OpenOptions(store));
        key.Delete();
        return true;
    }

    private static CngKeyOpenOptions OpenOptions(ServerKeyStore store) =>
        store == ServerKeyStore.Machine ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None;
}
