using Microsoft.EntityFrameworkCore;
using Simple.Server.Persistence;

namespace Simple.Server.Accounts;

/// <summary>
/// <c>Simple.Server set-admin-password</c>: sets the administrator's password
/// on the server machine, at first install and when it is forgotten (ADR
/// 0045). There is no other way: no page or endpoint sets it. The password
/// is read from standard input, never from the command line, which other
/// users of the machine can see. Takes the server's own settings (instance,
/// connection string, settings file), migrates the database first, and ends
/// every login of the administrator; a running server drops them within
/// <see cref="LoginHousekeeping.Period"/>.
/// </summary>
public static class AdminPasswordCommand
{
    public const string Name = "set-admin-password";

    public static async Task<int> RunAsync(IServiceProvider services, TextReader input, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        var password = await input.ReadLineAsync(cancellationToken);
        if (Passwords.Check(password) is { } problem)
        {
            await output.WriteLineAsync($"The administrator's password was not set: {problem} Give it on standard input, as one line.");
            return 2;
        }

        await SetAsync(services, password!, cancellationToken);
        await output.WriteLineAsync("The administrator's password is set; every login of the administrator has ended.");
        return 0;
    }

    /// <summary>
    /// Standard input, or, when a person types at a console, what they type
    /// without showing it on the screen.
    /// </summary>
    public static TextReader Input()
    {
        if (Console.IsInputRedirected)
        {
            return Console.In;
        }

        Console.Error.Write("The administrator's new password (not shown): ");
        var typed = new System.Text.StringBuilder();
        while (Console.ReadKey(intercept: true) is var key && key.Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                typed.Length = Math.Max(0, typed.Length - 1);
            }
            else if (!char.IsControl(key.KeyChar))
            {
                typed.Append(key.KeyChar);
            }
        }

        Console.Error.WriteLine();
        return new StringReader(typed.ToString());
    }

    public static async Task SetAsync(IServiceProvider services, string password, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SimpleDbContext>().Database.MigrateAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<AccountChanges>().SetAdminPasswordAsync(password, cancellationToken);
    }
}
