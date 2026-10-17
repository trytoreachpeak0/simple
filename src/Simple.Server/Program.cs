using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Simple.Server.Configuration;
using Simple.Server.Events;
using Simple.Server.Mes;
using Simple.Server.OnboardTerminals;
using Simple.Server.Persistence;
using Simple.Server.Riot;
using Simple.Server.Status;

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
        await Console.Error.WriteLineAsync($"The server refuses to start: Simple:SettingsFile '{path}' does not exist.");
        return 2;
    }

    builder.Configuration.AddJsonFile(path, optional: false, reloadOnChange: false);
    builder.Configuration.AddEnvironmentVariables();
    builder.Configuration.AddCommandLine(args);
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
builder.Services.AddGrpc();
builder.WebHost.ConfigureKestrel(kestrel => kestrel.ApplicationServices.GetRequiredService<OnboardEndpoint>().Configure(kestrel));
builder.Services.AddHealthChecks().AddDbContextCheck<SimpleDbContext>(tags: ["ready"]);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

// Building already reads the settings (Kestrel binds from them), so an
// invalid configuration can surface here as well as at start.
try
{
    var app = builder.Build();

    app.UseRouting();
    app.Use(OnboardEndpoint.KeepToOwnListener);
    app.MapGrpcService<OnboardLinkService>();
    app.MapOpenApi();
    // Live: the process answers. Ready: it can also reach its database.
    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
    app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
    app.MapStatusEndpoints();
    app.MapEventEndpoints();

    await app.RunAsync();
    return 0;
}
catch (System.Security.Cryptography.CryptographicException e)
{
    // For example a machine key store the account may not create keys in.
    var settings = builder.Configuration;
    await Console.Error.WriteLineAsync(
        $"The server refuses to start: it cannot open or create its terminal certificate key {settings[ServerSettings.ServerKeyNameKey] ?? $"Simple-Server-{settings[ServerSettings.InstanceKey]}"} "
        + $"in the {settings[ServerSettings.ServerKeyStoreKey] ?? "User"} key store: {e.Message}");
    return 2;
}
catch (OptionsValidationException e)
{
    await Console.Error.WriteLineAsync("The server refuses to start: the configuration is invalid.");
    foreach (var failure in e.Failures)
    {
        await Console.Error.WriteLineAsync($"  - {failure}");
    }

    return 2;
}
