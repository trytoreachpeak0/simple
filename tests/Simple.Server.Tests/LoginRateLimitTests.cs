using System.Net;
using Simple.Server.Accounts;

namespace Simple.Server.Tests;

/// <summary>Password guessing is slowed per computer, and no account is ever locked (ADR 0045).</summary>
public sealed class LoginRateLimitTests
{
    [Fact]
    public async Task AComputerThatTriesTooOftenWaitsWhileOtherComputersAndTheAccountAreUnaffected()
    {
        await using var server = new ServerInstance(extraSettings: new Dictionary<string, string?>
        {
            [$"{LoginSettings.Section}:AttemptsPerWindow"] = "3",
            [$"{LoginSettings.Section}:AttemptWindow"] = "00:10:00",
        });
        server.Start();

        var guesses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            guesses.Add((await server.LogInAsync("admin", $"guess-{i}-wrong", remoteAddress: "10.0.0.66")).Response.StatusCode);
        }

        var limited = await server.LogInAsync("admin", ServerInstance.AdminPassword, remoteAddress: "10.0.0.66");
        var elsewhere = await server.LogInAsync("admin", ServerInstance.AdminPassword, remoteAddress: "10.0.0.7");

        Assert.All(guesses, s => Assert.Equal(HttpStatusCode.Unauthorized, s));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Response.StatusCode);
        Assert.True(limited.Response.Headers.RetryAfter is not null, "A refused attempt says when to try again.");
        Assert.Equal(HttpStatusCode.OK, elsewhere.Response.StatusCode);
    }
}
