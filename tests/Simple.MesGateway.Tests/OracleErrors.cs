using System.Reflection;
using Oracle.ManagedDataAccess.Client;

namespace Simple.MesGateway.Tests;

/// <summary>
/// Real <see cref="OracleException"/>s with a chosen error number, as the
/// driver throws them. The driver has no public constructor for one, so this
/// uses its own (errCode, dataSrc, procedure, errMsg, parseErrorOffset).
/// </summary>
internal static class OracleErrors
{
    private static readonly ConstructorInfo Constructor =
        typeof(OracleException).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(int), typeof(string), typeof(string), typeof(string), typeof(int)])
        ?? throw new InvalidOperationException("Oracle's driver no longer has the OracleException constructor these tests use.");

    public static OracleException Create(int number, string message)
    {
        var error = (OracleException)Constructor.Invoke([number, "", "", message, 0]);
        if (error.Number != number)
        {
            throw new InvalidOperationException($"The OracleException made for {number} reports {error.Number}.");
        }

        return error;
    }
}
