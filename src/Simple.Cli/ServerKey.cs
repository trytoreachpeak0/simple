using System.Security.Cryptography;

namespace Simple.Cli;

/// <summary>
/// The persisted key a local server makes its terminal certificate from (see
/// Simple.Server's OnboardTerminals.ServerCertificate), in the current user's
/// key store. Only down --reset and the tests' clean-up delete it.
/// </summary>
public static class ServerKey
{
    private static readonly CngProvider Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider;

    public static bool Exists(string name) => CngKey.Exists(name, Provider);

    /// <returns>Whether there was a key to delete.</returns>
    public static bool Delete(string name)
    {
        if (!Exists(name))
        {
            return false;
        }

        using var key = CngKey.Open(name, Provider);
        key.Delete();
        return true;
    }
}
