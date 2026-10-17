using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Simple.Logging;
using Simple.Server.Accounts;
using Simple.Server.Configuration;
using Simple.Server.Events;
using Simple.Server.Listeners;
using Simple.Server.Mes;
using Simple.Server.OnboardTerminals;
using Simple.Server.Persistence;
using Simple.Server.Riot;
using Simple.Server.Status;

// Server commands run with the server's own settings and exit:
// set-admin-password, the one way to set the administrator's password
// (ADR 0045); export-console-certificate <file> and
// discard-console-certificate (ADR 0046).
string[] commands = [AdminPasswordCommand.Name, ConsoleCertificateCommands.ExportName, ConsoleCertificateCommands.DiscardName];
var command = commands.Contains(args.FirstOrDefault()) ? args[0] : null;
string? commandFile = null;
if (command is not null)
{
    args = args[1..];
    if (command == ConsoleCertificateCommands.ExportName && args.Length > 0 && !args[0].StartsWith('-'))
    {
        commandFile = args[0];
        args = args[1..];
    }
}

var builder = WebApplication.CreateBuilder(args);

// Real endpoints and credentials live outside the repository. A settings file
// named by Simple:SettingsFile (--Simple:SettingsFile=<path> or the
// environment variable Simple__SettingsFile) overrides appsettings*.json;
// environment variables and the command line still have the last word.
var settingsFile = builder.Configuration["Simple:SettingsFile"];
if (!string.IsNullOrEmpty(settingsFile))
{
    var path = Path.GetFullPath(settingsFile);
    if (!File.Exists(path))
    {
        return await ProgramLog.RefuseToStartAsync(
            "Server", builder.Configuration, null, $"The server refuses to start: Simple:SettingsFile '{path}' does not exist.");
    }

    builder.Configuration.AddJsonFile(path, optional: false, reloadOnChange: false);
    builder.Configuration.AddEnvironmentVariables();
    builder.Configuration.AddCommandLine(args);
}

// The program log (ADR 0034): a size-limited file under Simple:Logs:Directory
// (default %ProgramData%\Simple\Server\logs) and the console, both redacted;
// unhandled exceptions are recorded too.
try
{
    builder.Logging.AddProgramLog("Server", builder.Configuration, console: true);
}
catch (InvalidDataException e)
{
    await Console.Error.WriteLineAsync($"The server refuses to start: {e.Message}");
    return 2;
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<ServerSettings>()
    .Configure<IConfiguration>((settings, configuration) => settings.ReadFrom(configuration))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ServerSettings>, ServerSettingsValidator>();
builder.Services.AddDbContext<SimpleDbContext>((services, options) =>
    options.UseSqlServer(services.GetRequiredService<IOptions<ServerSettings>>().Value.DatabaseConnectionString));
builder.Services.AddScoped<EventStore>();
builder.Services.AddSingleton<ServerInfo>();
builder.Services.AddHostedService<ServerLifecycleRecorder>();
// After the recorder, which migrates the database: the logins and the console
// certificate are read from it.
builder.Services.AddLogin();
builder.Services.AddSingleton<ConsoleEndpoint>();
builder.Services.AddHostedService<ConsoleCertificateLoader>();
builder.Services.AddScoped<AccountChanges>();
builder.Services.AddOptions<MesGatewaySettings>()
    .BindConfiguration(MesGatewaySettings.Section)
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<MesGatewaySettings>, MesGatewaySettingsValidator>();
builder.Services.AddHttpClient(MesGatewayPoller.HttpClientName)
    .ConfigureHttpClient((services, client) =>
        client.Timeout = services.GetRequiredService<IOptions<MesGatewaySettings>>().Value.RequestTimeout);
builder.Services.AddSingleton<MesGatewayMonitor>();
builder.Services.AddHostedService<MesGatewayPoller>();
builder.Services.AddRiotConnection();
builder.Services.AddSingleton<OnboardEndpoint>();
builder.Services.AddSingleton<OnboardTerminalRegistry>();
// No catch-all "unimplemented" endpoints: they would answer without login
// and are not the terminal protocol (Access.OnboardTerminal).
builder.Services.AddGrpc(options => options.IgnoreUnknownServices = true);
// A server command listens nowhere: binding would open the terminal
// certificate's key, in the key store of whoever runs the command.
if (command is null)
{
    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.ApplicationServices.GetRequiredService<OnboardEndpoint>().Configure(kestrel);
        kestrel.ApplicationServices.GetRequiredService<ConsoleEndpoint>().Configure(kestrel);
    });
}

builder.Services.AddHealthChecks().AddDbContextCheck<SimpleDbContext>(tags: ["ready"]);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

// Building already reads the settings (Kestrel binds from them), so an
// invalid configuration can surface here as well as at start. The app is
// disposed only after a refusal is in its log.
WebApplication? app = null;
try
{
    app = builder.Build();
    if (command is not null)
    {
        return command switch
        {
            ConsoleCertificateCommands.ExportName => await ConsoleCertificateCommands.ExportAsync(app.Services, commandFile, Console.Out, CancellationToken.None),
            ConsoleCertificateCommands.DiscardName => await ConsoleCertificateCommands.DiscardAsync(app.Services, Console.Out, CancellationToken.None),
            _ => await AdminPasswordCommand.RunAsync(app.Services, AdminPasswordCommand.Input(), Console.Out, CancellationToken.None),
        };
    }

    app.UseRouting();
    app.Use(Listener.KeepToOwnListener);
    app.UseLogin();
    // Every endpoint names who may use it (Access, ADR 0014, 0045); one that
    // names nobody is closed to everyone.
    app.MapGrpcService<OnboardLinkService>().RequireAccess(Access.OnboardTerminal);
    app.MapOpenApi().RequireAccess(Access.View);
    // Live: the process answers. Ready: it can also reach its database.
    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).RequireAccess(Access.Anonymous);
    app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).RequireAccess(Access.View);
    app.MapLoginEndpoints();
    app.MapAccountEndpoints();
    app.MapStatusEndpoints();
    app.MapEventEndpoints();

    await app.StartAsync();
    await app.WaitForShutdownAsync();
    return 0;
}
catch (System.Security.Cryptography.CryptographicException e)
{
    // For example a machine key store the account may not create keys in.
    var settings = builder.Configuration;
    return await ProgramLog.RefuseToStartAsync(
        "Server",
        builder.Configuration,
        app?.Services,
        $"The server refuses to start: it cannot open or create its terminal certificate key {settings[ServerSettings.ServerKeyNameKey] ?? $"Simple-Server-{settings[ServerSettings.InstanceKey]}"} "
        + $"in the {settings[ServerSettings.ServerKeyStoreKey] ?? "User"} key store: {e.Message}");
}
catch (ServerRefusesToStartException e)
{
    return await ProgramLog.RefuseToStartAsync("Server", builder.Configuration, app?.Services, e.Message);
}
catch (OptionsValidationException e)
{
    return await ProgramLog.RefuseToStartAsync(
        "Server", builder.Configuration, app?.Services, "The server refuses to start: the configuration is invalid.", e.Failures.ToList());
}
finally
{
    if (app is not null)
    {
        await app.DisposeAsync();
    }
}
