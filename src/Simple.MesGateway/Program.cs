using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Simple.Logging;
using Simple.MesGateway.Configuration;
using Simple.MesGateway.Fake;
using Simple.MesGateway.Rounds;
using Simple.MesGateway.Sources;

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
            "MesGateway", builder.Configuration, null, $"The MES gateway refuses to start: Simple:SettingsFile '{path}' does not exist.");
    }

    builder.Configuration.AddJsonFile(path, optional: false, reloadOnChange: false);
    builder.Configuration.AddEnvironmentVariables();
    builder.Configuration.AddCommandLine(args);
}

// The program log (ADR 0034): a size-limited file under Simple:Logs:Directory
// (default %ProgramData%\Simple\MesGateway\logs) and the console, both redacted;
// unhandled exceptions are recorded too.
try
{
    builder.Logging.AddProgramLog("MesGateway", builder.Configuration, console: true);
}
catch (InvalidDataException e)
{
    await Console.Error.WriteLineAsync($"The MES gateway refuses to start: {e.Message}");
    return 2;
}

var configured = new GatewaySettings();
configured.ReadFrom(builder.Configuration);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<GatewaySettings>()
    .Configure<IConfiguration>((settings, configuration) => settings.ReadFrom(configuration))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<GatewaySettings>, GatewaySettingsValidator>();
if (configured.UsesFakeSource)
{
    builder.Services.AddSingleton<FakeMesSource>();
    builder.Services.AddSingleton<IMesSource>(services => services.GetRequiredService<FakeMesSource>());
}

builder.Services.AddSingleton<SnapshotStore>();
builder.Services.AddHostedService<RoundReader>();
builder.Services.AddHealthChecks().AddCheck<FirstRoundHealthCheck>("first-round", tags: ["ready"]);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.MapOpenApi();
// Live: the process answers. Ready: it has finished its first round, good or not.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapSnapshotEndpoints();
// The control API exists only with the fake source behind it.
if (configured.UsesFakeSource)
{
    app.MapFakeMesEndpoints();
}

try
{
    // Validate before the hosted services are built: without a known source
    // there is nothing to build the round reader from, and the reason must
    // be the setting, not a missing service.
    _ = app.Services.GetRequiredService<IOptions<GatewaySettings>>().Value;
    await app.RunAsync();
    return 0;
}
catch (OptionsValidationException e)
{
    return await ProgramLog.RefuseToStartAsync(
        "MesGateway", builder.Configuration, app.Services, "The MES gateway refuses to start: the configuration is invalid.", e.Failures.ToList());
}
