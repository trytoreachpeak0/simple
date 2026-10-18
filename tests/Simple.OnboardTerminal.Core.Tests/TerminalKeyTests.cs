using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Simple.OnboardTerminal.Core.Tests;

/// <summary>
/// The terminal's own key (ADR 0025), in the key store the tests were told
/// (<see cref="TestKeyStore"/>), under names the remote machine's sweep knows
/// (<see cref="TestKeys"/>); every test deletes what it creates.
/// </summary>
public sealed class TerminalKeyTests
{
    [Fact]
    public void TheKeyIsCreatedOnceNotExportableAndOpenedByNameOnEveryLaterStart()
    {
        var name = TestKeys.NewName();
        try
        {
            string pin;
            using (var first = TerminalKey.OpenOrCreate(name, TestKeyStore.IsMachine, tryTpm: false, out var tpmProblem))
            {
                Assert.True(first.Created);
                Assert.Null(tpmProblem);
                Assert.Equal(CngProvider.MicrosoftSoftwareKeyStorageProvider.Provider, first.Provider);
                Assert.Equal(TestKeyStore.IsMachine, first.MachineKey);
                using var key = CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, TestKeyStore.IsMachine ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None);
                Assert.Equal(CngExportPolicies.None, key.ExportPolicy);
                Assert.Throws<CryptographicException>(() => key.Export(CngKeyBlobFormat.EccPrivateBlob));

                // The certificate is bound to the persisted key by name: nothing imported, nothing temporary.
                using var bound = first.Certificate.GetECDsaPrivateKey();
                Assert.Equal(name, Assert.IsType<ECDsaCng>(bound).Key.KeyName);
                pin = first.Pin;
            }

            using var again = TerminalKey.OpenOrCreate(name, TestKeyStore.IsMachine, tryTpm: false, out _);
            Assert.False(again.Created);
            Assert.Equal(pin, again.Pin);
        }
        finally
        {
            TerminalKey.Delete(name, TestKeyStore.IsMachine);
        }

        Assert.False(TerminalKey.Exists(name, TestKeyStore.IsMachine));
    }

    [Fact]
    public void ADeletedKeyIsCreatedAnewWithAnotherPin()
    {
        var name = TestKeys.NewName();
        try
        {
            string pin;
            using (var first = TerminalKey.OpenOrCreate(name, TestKeyStore.IsMachine, tryTpm: false, out _))
            {
                pin = first.Pin;
            }

            Assert.True(TerminalKey.Delete(name, TestKeyStore.IsMachine));
            Assert.False(TerminalKey.Delete(name, TestKeyStore.IsMachine));
            using var next = TerminalKey.OpenOrCreate(name, TestKeyStore.IsMachine, tryTpm: false, out _);
            Assert.True(next.Created);
            Assert.NotEqual(pin, next.Pin);
        }
        finally
        {
            TerminalKey.Delete(name, TestKeyStore.IsMachine);
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void AKeyThatCannotBeCreatedIsRefusedSayingWhatToDo(bool machineKey, bool overSsh)
    {
        var message = TerminalKey.CreateFailed("Simple-OnboardTerminal", machineKey, "Access is denied.", overSsh);

        Assert.Contains("Simple-OnboardTerminal", message, StringComparison.Ordinal);
        Assert.Contains("Access is denied.", message, StringComparison.Ordinal);
        if (machineKey)
        {
            Assert.Contains("administrator", message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("DPAPI", message, StringComparison.Ordinal);
            Assert.Contains("no script over SSH", message, StringComparison.Ordinal);
            Assert.Equal(overSsh, message.Contains("SSH_CONNECTION is set", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The trap of ADR 0025, for real where it can be met: an SSH key logon
    /// (simple remote's) cannot create a key in the user's store. Elsewhere it
    /// is not applicable, and an SSH session that can create one says so.
    /// </summary>
    [Fact]
    public void OverSshAUserKeyCannotBeCreatedAndTheTerminalSaysWhy()
    {
        if (Environment.GetEnvironmentVariable("SSH_CONNECTION") is not { Length: > 0 })
        {
            Assert.Skip("Not in an SSH session: the user's key store works here.");
        }

        var name = TestKeys.NewName();
        try
        {
            var refusal = Assert.Throws<TerminalKeyException>(() => TerminalKey.OpenOrCreate(name, machineKey: false, tryTpm: false, out _).Dispose());
            Assert.Contains("DPAPI", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("SSH_CONNECTION is set", refusal.Message, StringComparison.Ordinal);
        }
        finally
        {
            TerminalKey.Delete(name, machineKey: false);
        }
    }

    [Fact]
    public void WhereTheComputerHasATpmTheKeyGoesIntoIt()
    {
        var name = TestKeys.NewName();
        try
        {
            using var key = TerminalKey.OpenOrCreate(name, TestKeyStore.IsMachine, tryTpm: true, out var tpmProblem);
            if (!key.InTpm)
            {
                // Then it says why, and the key is in the software store instead.
                Assert.NotNull(tpmProblem);
                Assert.Equal(CngProvider.MicrosoftSoftwareKeyStorageProvider.Provider, key.Provider);
                Assert.Skip($"No TPM this account can use here: {tpmProblem}");
            }

            Assert.Null(tpmProblem);
            using var again = TerminalKey.OpenOrCreate(name, TestKeyStore.IsMachine, tryTpm: true, out _);
            Assert.True(again.InTpm);
            Assert.False(again.Created);
            Assert.Equal(key.Pin, again.Pin);
        }
        finally
        {
            TerminalKey.Delete(name, TestKeyStore.IsMachine);
        }

        Assert.False(TerminalKey.Exists(name, TestKeyStore.IsMachine));
    }
}

/// <summary>Terminal keys for tests: Simple-Test- and 32 hex digits, the names the remote machine's sweep deletes when a run left them behind.</summary>
public static class TestKeys
{
    public static string NewName() => $"Simple-Test-{Guid.NewGuid():N}";

    /// <summary>A new key in the software store the tests were told to use.</summary>
    public static TerminalKey Create() => TerminalKey.OpenOrCreate(NewName(), TestKeyStore.IsMachine, tryTpm: false, out _);

    public static void Delete(TerminalKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        key.Dispose();
        TerminalKey.Delete(key.Name, key.MachineKey);
    }
}
