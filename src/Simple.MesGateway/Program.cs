using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
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
        await Console.Error.WriteLineAsync($"The MES gateway refuses to start: Simple:SettingsFile '{path}' does not exist.");
        return 2;
    }

    builder.Configuration.AddJsonFile(path, optional: false, reloadOnChange: false);
    builder.Configuration.AddEnvironmentVariables();
    builder.Configuration.AddCommandLine(args);
}

var configured = new GatewaySettings();
configured.ReadFrom(builder.Configuration);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<GatewaySettings>()
    .Configure<IConfiguration>((settings, configuration) => settings.ReadFrom(configuration))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<GatewaySettings>, GatewaySettingsValidator>();
builder.Services.AddSingleton<FakeMesSource>();
builder.Services.AddSingleton<IMesSource>(services => services.GetRequiredService<FakeMesSource>());
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
if (configured.UsesFakeSource)
{
    app.MapFakeMesEndpoints();
}

try
{
    await app.RunAsync();
    return 0;
}
catch (OptionsValidationException e)
{
    await Console.Error.WriteLineAsync("The MES gateway refuses to start: the configuration is invalid.");
    foreach (var failure in e.Failures)
    {
        await Console.Error.WriteLineAsync($"  - {failure}");
    }

    return 2;
}
