using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Simple.OnboardTerminal.Core;

namespace Simple.OnboardTerminal.Headless;

/// <summary>
/// The test input: a small HTTP interface, opened only in test mode
/// (<c>--test-input=http://127.0.0.1:&lt;port&gt;</c>) and only on loopback,
/// through which tests, the command line and AI sessions do what a person at
/// the vehicle would. What goes in here reaches the next report exactly as a
/// scanner or a button would put it there.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>POST /test/keyboard {"text": "LOT123\r"}</c>: keystrokes, as a barcode scanner types them; every Enter ends a scan. 200 with the scans completed, 409 while disconnected (the keystrokes are dropped, as on the real screen).</item>
/// <item><c>POST /test/press {"button": "..."}</c>: a button pressed. 200 with its input id, 409 while disconnected.</item>
/// <item><c>GET /test/status</c>: the connection's state and the screen text.</item>
/// <item><c>GET /test/ready</c>: 200 while connected, otherwise 503.</item>
/// </list>
/// </remarks>
internal sealed class TestInput(TestInputOptions options, OnboardInputs inputs, TerminalConnection connection) : IHostedService, IAsyncDisposable
{
    private WebApplication? app;

    public static bool TryParse(string text, out Uri? address)
    {
        address = Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttp
            && !uri.IsDefaultPort
            && uri.AbsolutePath == "/"
            && (uri.IsLoopback && (uri.HostNameType != UriHostNameType.Dns || uri.Host == "localhost"))
                ? uri
                : null;
        return address is not null;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        // Nothing around the process may add a listener: the builder has read
        // environment variables (Kestrel__Endpoints__… among them) and
        // appsettings by now, and Kestrel would bind what they name besides
        // the loopback address below.
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            if (options.Address.Host == "localhost")
            {
                kestrel.ListenLocalhost(options.Address.Port);
            }
            else
            {
                kestrel.Listen(IPAddress.Parse(options.Address.Host.Trim('[', ']')), options.Address.Port);
            }
        });
        app = builder.Build();
        app.MapGet("/test/ready", () => connection.Status.State == TerminalState.Connected ? Results.Ok() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        app.MapGet("/test/status", () => Results.Ok(connection.Status));
        app.MapPost("/test/keyboard", (KeyboardRequest request) =>
            inputs.Type(request.Text ?? string.Empty) is { } scans
                ? Results.Ok(new { Scans = scans.Select(s => new { s.Id, s.Text }) })
                : Disconnected());
        app.MapPost("/test/press", (PressRequest request) =>
            string.IsNullOrWhiteSpace(request.Button)
                ? Results.BadRequest(new { Error = "button is missing" })
                : inputs.Press(request.Button) is { } id
                    ? Results.Ok(new { Id = id })
                    : Disconnected());
        await app.StartAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (app is not null)
        {
            await app.StopAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null)
        {
            await app.DisposeAsync();
        }
    }

    private IResult Disconnected() =>
        Results.Conflict(new { Error = "the terminal is not connected: inputs are dropped, as on the real screen", connection.Status });

    internal sealed record KeyboardRequest(string? Text);

    internal sealed record PressRequest(string? Button);
}

/// <param name="Address">Where the test input listens: http:// on loopback.</param>
internal sealed record TestInputOptions(Uri Address);
