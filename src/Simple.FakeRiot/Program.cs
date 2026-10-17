using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Simple.FakeRiot;

var builder = WebApplication.CreateBuilder(args);

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
    await Console.Error.WriteLineAsync("The fake fleet manager refuses to start: the configuration is invalid.");
    foreach (var failure in e.Failures)
    {
        await Console.Error.WriteLineAsync($"  - {failure}");
    }

    return 2;
}

/// <summary>The fake's program; public so tests can host it in process.</summary>
public partial class Program;
