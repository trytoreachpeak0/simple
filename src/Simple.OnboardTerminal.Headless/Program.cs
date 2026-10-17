using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Simple.OnboardTerminal.Core;
using Simple.OnboardTerminal.Headless;

// The log goes to a file when the command line starts the terminal; screen
// texts are Chinese.
Console.OutputEncoding = System.Text.Encoding.UTF8;

// Settings come from the terminal's own file (--config, default under
// %ProgramData%), never from environment variables or appsettings, so that
// nothing around the process can point the terminal elsewhere.
var arguments = new ConfigurationBuilder().AddCommandLine(args).Build();
var configPath = arguments["config"] is { Length: > 0 } given ? Path.GetFullPath(given) : TerminalSettings.DefaultPath;

TerminalSettings settings;
try
{
    settings = TerminalSettings.Load(configPath);
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
{
    await Console.Error.WriteLineAsync($"The onboard terminal refuses to start: cannot read its settings {configPath}: {e.Message}");
    return 2;
}

var problems = settings.Problems().ToList();
Uri? testInput = null;
if (arguments["test-input"] is { Length: > 0 } testInputText && !TestInput.TryParse(testInputText, out testInput))
{
    problems.Add($"--test-input '{testInputText}' is not an http:// address on loopback (127.0.0.1, [::1] or localhost) with a port.");
}

if (problems.Count > 0)
{
    await Console.Error.WriteLineAsync($"The onboard terminal refuses to start ({configPath}):");
    foreach (var problem in problems)
    {
        await Console.Error.WriteLineAsync($"  - {problem}");
    }

    return 2;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = [],
    ContentRootPath = AppContext.BaseDirectory,
    DisableDefaults = true,
});
builder.Logging.AddSimpleConsole(console =>
{
    console.SingleLine = true;
    console.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
});
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("Grpc", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<OnboardInputs>();
builder.Services.AddSingleton(services => new TerminalConnection(
    settings,
    services.GetRequiredService<OnboardInputs>(),
    services.GetRequiredService<TimeProvider>(),
    services.GetRequiredService<ILoggerFactory>()));
builder.Services.AddHostedService<TerminalService>();
if (testInput is not null)
{
    builder.Services.AddSingleton(new TestInputOptions(testInput));
    builder.Services.AddHostedService<TestInput>();
}

using var host = builder.Build();
await host.RunAsync();
return 0;
