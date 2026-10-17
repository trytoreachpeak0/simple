using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Simple.Server.Accounts;
using Simple.Server.Events;

namespace Simple.Server.Tests;

/// <summary>The server command that sets the administrator's password, the only way to set it (ADR 0045).</summary>
public sealed class AdminPasswordCommandTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheCommandSetsThePasswordFromStandardInputAndEndsTheRunningServersAdministratorLogins()
    {
        await using var server = new ServerInstance();
        var before = server.Start();
        var output = new StringWriter();

        var exit = await AdminPasswordCommand.RunAsync(server.Services, new StringReader("a-new-admin-password\n"), output, Cancel);

        Assert.Equal(0, exit);
        Assert.Equal(HttpStatusCode.OK, (await server.LogInAsync("admin", "a-new-admin-password")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.LogInAsync("admin", ServerInstance.AdminPassword)).Response.StatusCode);
        // The command runs in another process; the server drops the old logins with its next flush.
        var deadline = DateTimeOffset.UtcNow + LoginHousekeeping.Period * 3;
        HttpStatusCode status;
        do
        {
            status = (await before.GetAsync(new Uri("/api/v1/status", UriKind.Relative), Cancel)).StatusCode;
        }
        while (status == HttpStatusCode.OK && DateTimeOffset.UtcNow < deadline && await Delay());

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        var admin = (await server.LogInAsync("admin", "a-new-admin-password")).Client!;
        var events = await admin.GetFromJsonAsync<EventPage>(new Uri("/api/v1/events", UriKind.Relative), Cancel);
        var set = Assert.Single(events!.Items, e => e.Type == EventTypes.AccountPasswordSet);
        Assert.Equal(EventSources.ServerCommand, set.Source);
        Assert.DoesNotContain("a-new-admin-password", set.Data!.Value.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATooShortPasswordChangesNothing()
    {
        await using var server = new ServerInstance();
        server.Start();
        var output = new StringWriter();

        var exit = await AdminPasswordCommand.RunAsync(server.Services, new StringReader("short\n"), output, Cancel);

        Assert.Equal(2, exit);
        Assert.Contains("at least 8 and at most 128 characters", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await server.LogInAsync("admin", ServerInstance.AdminPassword)).Response.StatusCode);
    }

    [Fact]
    public async Task TheServerProgramRunsTheCommandFromStandardInputAndOpensNoKeyOfWhoeverRunsIt()
    {
        await using var server = new ServerInstance();
        var keyName = $"Simple-Test-{Guid.NewGuid():N}";
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Simple.Server.exe"))
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // With a terminal endpoint configured, as on the server machine.
        foreach (var argument in new[]
        {
            AdminPasswordCommand.Name,
            $"--Simple:Instance={server.Instance}",
            $"--ConnectionStrings:Simple={ServerInstance.SqlServer}",
            $"--Simple:Logs:Directory={server.LogDirectory}",
            "--Simple:OnboardTerminals:Url=https://127.0.0.1:0",
            $"--Simple:OnboardTerminals:ServerKeyName={keyName}",
            $"--Simple:OnboardTerminals:ServerKeyStore={TestKeyStore.Name}",
        })
        {
            start.ArgumentList.Add(argument);
        }

        string output;
        int exit;
        var keyMade = false;
        try
        {
            using var process = System.Diagnostics.Process.Start(start)!;
            var reading = process.StandardOutput.ReadToEndAsync(Cancel);
            var errors = process.StandardError.ReadToEndAsync(Cancel);
            await process.StandardInput.WriteLineAsync("a-password-from-stdin");
            process.StandardInput.Close();
            await process.WaitForExitAsync(Cancel);
            output = await reading + await errors;
            exit = process.ExitCode;
        }
        finally
        {
            if (OperatingSystem.IsWindows())
            {
                keyMade = Simple.Server.OnboardTerminals.ServerCertificate.Delete(keyName, TestKeyStore.IsMachine);
            }
        }

        Assert.True(exit == 0, output);
        Assert.Contains("The administrator's password is set", output, StringComparison.Ordinal);
        Assert.False(keyMade, "The command opened or created the terminal certificate key.");
        await using var db = server.CreateDbContext();
        var admin = await db.Accounts.SingleAsync(a => a.Role == AccountRole.Admin, Cancel);
        Assert.NotEqual(
            PasswordVerificationResult.Failed,
            new PasswordHasher<Account>().VerifyHashedPassword(admin, admin.PasswordHash, "a-password-from-stdin"));
    }

    private static async Task<bool> Delay()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(250), Cancel);
        return true;
    }
}
