using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Serilog;
using Serilog.Events;
using Simple.ProtocolProbe.Server;

var builder = WebApplication.CreateBuilder(args);

// Only the command line configures the probe: no environment variables, no
// appsettings, and no Kestrel endpoints or URLs from configuration, so that
// nothing but the one TLS listener below can ever be opened.
builder.Configuration.Sources.Clear();
builder.Configuration.AddCommandLine(args);
var foreign = builder.Configuration.AsEnumerable()
    .Select(pair => pair.Key)
    .Where(key => key.StartsWith("Kestrel", StringComparison.OrdinalIgnoreCase)
        || key.Equals("urls", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("_ports", StringComparison.OrdinalIgnoreCase))
    .ToList();
if (foreign.Count > 0)
{
    await Console.Error.WriteLineAsync($"refusing to start: Kestrel endpoints and URLs are fixed by the probe; remove {string.Join(", ", foreign)}");
    return 2;
}

ProbeServerOptions options;
try
{
    options = ProbeServerOptions.From(builder.Configuration);
}
catch (ArgumentException ex)
{
    await Console.Error.WriteLineAsync($"refusing to start: {ex.Message}");
    return 2;
}

Directory.CreateDirectory(options.DataDirectory);

var loggerConfig = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore.Server.Kestrel", LogEventLevel.Debug)
    .MinimumLevel.Override("Microsoft.AspNetCore.Hosting", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore.Routing", LogEventLevel.Warning)
    .MinimumLevel.Override("Grpc", LogEventLevel.Information)
    .WriteTo.File(options.LogFile, outputTemplate: ProbeServerOptions.LogTemplate, formatProvider: CultureInfo.InvariantCulture, flushToDiskInterval: TimeSpan.FromSeconds(1));
if (options.ConsoleLog)
{
    loggerConfig = loggerConfig.WriteTo.Console(outputTemplate: ProbeServerOptions.LogTemplate, formatProvider: CultureInfo.InvariantCulture);
}

Log.Logger = loggerConfig.CreateLogger();
builder.Host.UseSerilog();

try
{
    // Disposing the certificate at the end of the run removes the temporary key
    // container the PFX was imported into; a killed process leaves it behind.
    using var certificate = ServerCertificate.LoadOrCreate(options.DataDirectory, options.KeyAlgorithm, options.KeyLoad);
    Log.Information(
        "START server pid={Pid} os={Os} runtime={Runtime} arch={Arch} bind={Bind}:{Port} tls={Tls} data={Data} log={LogFile}",
        Environment.ProcessId, RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription,
        RuntimeInformation.ProcessArchitecture, options.Bind, options.Port, options.Tls, options.DataDirectory, options.LogFile);
    File.WriteAllText(Path.Combine(options.DataDirectory, "server-pin.txt"), ServerCertificate.Pin(certificate));
    Log.Information(
        "SERVER-CERT subject={Subject} keyAlg={KeyAlg} keyLoad={KeyLoad} keyContainer={Container} notAfter={NotAfter:o} pin(sha256 of SubjectPublicKeyInfo)={Pin}",
        certificate.Subject, options.KeyAlgorithm, options.KeyLoad, ServerCertificate.KeyContainer(certificate), certificate.NotAfter, ServerCertificate.Pin(certificate));

    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        if (options.PingDelay is { } pingDelay)
        {
            kestrel.Limits.Http2.KeepAlivePingDelay = pingDelay;
            kestrel.Limits.Http2.KeepAlivePingTimeout = options.PingTimeout;
        }

        kestrel.Listen(IPAddress.Parse(options.Bind), options.Port, listen =>
        {
            listen.Protocols = HttpProtocols.Http2;
            listen.UseHttps(https =>
            {
                https.ServerCertificate = certificate;
                https.SslProtocols = options.Tls == "12" ? SslProtocols.Tls12 : SslProtocols.None;
                https.ClientCertificateMode = options.RequireClientCertificate
                    ? ClientCertificateMode.RequireCertificate
                    : ClientCertificateMode.AllowCertificate;
                // Terminal keys are self-signed by design (ADR 0025); the key
                // itself is checked against the registry in TerminalHub.
                https.AllowAnyClientCertificate();
            });
        });
    });

    builder.Services.AddGrpc(grpc => grpc.Interceptors.Add<ProtocolVersionInterceptor>());
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton(new TerminalRegistry(Path.Combine(options.DataDirectory, "terminals.json")));
    builder.Services.AddSingleton<TerminalHub>();

    var app = builder.Build();
    app.MapGrpcService<ProbeV1Service>();
    app.MapGrpcService<ProbeV2Service>();

    var kestrelOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<KestrelServerOptions>>().Value;
    Log.Information(
        "KESTREL http2.keepAlivePingDelay={PingDelay} http2.keepAlivePingTimeout={PingTimeout} keepAliveTimeout={KeepAlive} minRequestBodyDataRate={MinReq} minResponseDataRate={MinResp}",
        kestrelOptions.Limits.Http2.KeepAlivePingDelay, kestrelOptions.Limits.Http2.KeepAlivePingTimeout,
        kestrelOptions.Limits.KeepAliveTimeout, Describe(kestrelOptions.Limits.MinRequestBodyDataRate),
        Describe(kestrelOptions.Limits.MinResponseDataRate));

    var hub = app.Services.GetRequiredService<TerminalHub>();
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    _ = Task.Run(() => OperatorConsole.RunAsync(hub, lifetime));
    _ = Task.Run(() => ResourceLog.RunAsync("server", () => hub.ActiveSessionCount, lifetime.ApplicationStopping));

    await app.RunAsync();
    Log.Information("STOP server pid={Pid}", Environment.ProcessId);
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "FATAL server stopped");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static string Describe(MinDataRate? rate) =>
    rate is null ? "off" : $"{rate.BytesPerSecond}B/s grace {rate.GracePeriod}";
