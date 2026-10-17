using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Simple.FakeRiot;
using Simple.Logging;

var builder = WebApplication.CreateBuilder(args);

// The program log (ADR 0034), like every SIMPLE program: a size-limited file
// under Simple:Logs:Directory (default %ProgramData%\Simple\FakeRiot\logs)
// and the console, both redacted; unhandled exceptions are recorded too.
try
{
    builder.Logging.AddProgramLog("FakeRiot", builder.Configuration, console: true);
}
catch (InvalidDataException e)
{
    await Console.Error.WriteLineAsync($"The fake fleet manager refuses to start: {e.Message}");
    return 2;
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<FakeRiotSettings>()
    .Configure<IConfiguration>((settings, configuration) => settings.ReadFrom(configuration))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<FakeRiotSettings>, FakeRiotSettingsValidator>();
builder.Services.AddSingleton(services =>
{
    var file = services.GetRequiredService<IOptions<FakeRiotSettings>>().Value.SeedFile;
    return Seed.Read(Path.IsPathRooted(file) ? file : Path.Combine(AppContext.BaseDirectory, file));
});
builder.Services.AddSingleton<FakeRiotWorld>();
builder.Services.AddHostedService<FakeRiotClock>();
builder.Services.AddHealthChecks();

try
{
    var app = builder.Build();
    _ = app.Services.GetRequiredService<IOptions<FakeRiotSettings>>().Value;

    app.MapHealthChecks("/health/live");
    app.MapHealthChecks("/health/ready", new HealthCheckOptions());
    app.UseRiotAuthentication();
    app.MapRiotEndpoints();
    app.MapFakeRiotControlEndpoints();

    await app.RunAsync();
    return 0;
}
catch (OptionsValidationException e)
{
    return await ProgramLog.RefuseToStartAsync(
        "FakeRiot", builder.Configuration, null, "The fake fleet manager refuses to start: the configuration is invalid.", e.Failures.ToList());
}

/// <summary>The fake's program; public so tests can host it in process.</summary>
public partial class Program;
