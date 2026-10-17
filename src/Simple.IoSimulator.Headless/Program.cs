using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Simple.IoSimulator.Headless;
using Simple.Logging;

var builder = WebApplication.CreateBuilder(args);

// The program log (ADR 0034), like every SIMPLE program: a size-limited file
// under Simple:Logs:Directory (default %ProgramData%\Simple\IoSimulator\logs)
// and the console, both redacted; unhandled exceptions are recorded too.
try
{
    builder.Logging.AddProgramLog("IoSimulator", builder.Configuration, console: true);
}
catch (InvalidDataException e)
{
    await Console.Error.WriteLineAsync($"The IO simulator refuses to start: {e.Message}");
    return 2;
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<IoSimulatorSettings>()
    .Configure<IConfiguration>((settings, configuration) => settings.ReadFrom(configuration))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<IoSimulatorSettings>, IoSimulatorSettingsValidator>();
builder.Services.AddSingleton<SimulatedIoModule>();
builder.Services.AddHealthChecks();
builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower)));

try
{
    var app = builder.Build();
    _ = app.Services.GetRequiredService<IOptions<IoSimulatorSettings>>().Value;
    // Listening on Modbus from the start, before the control API answers ready.
    _ = app.Services.GetRequiredService<SimulatedIoModule>();

    app.MapHealthChecks("/health/live");
    app.MapHealthChecks("/health/ready");
    app.MapControlEndpoints();

    await app.RunAsync();
    return 0;
}
catch (OptionsValidationException e)
{
    return await ProgramLog.RefuseToStartAsync(
        "IoSimulator", builder.Configuration, null, "The IO simulator refuses to start: the configuration is invalid.", e.Failures.ToList());
}

/// <summary>The simulator's program; public so tests can host it in process.</summary>
public partial class Program;
