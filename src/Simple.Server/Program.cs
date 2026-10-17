using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Simple.Server.Configuration;
using Simple.Server.Events;
using Simple.Server.Persistence;
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
builder.Services.AddHealthChecks().AddDbContextCheck<SimpleDbContext>(tags: ["ready"]);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.MapOpenApi();
// Live: the process answers. Ready: it can also reach its database.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapStatusEndpoints();
app.MapEventEndpoints();

try
{
    await app.RunAsync();
    return 0;
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
