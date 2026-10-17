using System.Globalization;

namespace Simple.Cli;

/// <summary>
/// Every component <c>simple up</c> starts, in start order (<c>down</c> stops
/// them in reverse). One entry per component: which project it is built
/// from, how many processes and ports it gets, what it is told on its
/// command line and how to tell it is ready. Adding a component to the local
/// system means adding an entry here.
/// </summary>
public static class Components
{
    public static IReadOnlyList<Component> All { get; } =
    [
        // Started before the server, which is told its address. up waits for
        // each component to be ready before it starts the next, so the
        // gateway answers by the server's first fetch. A gateway that goes
        // away later is the server's to notice: mes.unreachable, then
        // mes.recovered.
        new(
            Name: "mes-gateway",
            Project: "src/Simple.MesGateway/Simple.MesGateway.csproj",
            Count: _ => 1,
            PortsPerProcess: 1,
            Arguments: context =>
            [
                $"--urls={context.Url()}",
                "--Simple:Mes:Source=Fake",
            ],
            IsReady: (context, http, cancellationToken) =>
                Readiness.HttpOkAsync(http, new Uri(context.Url(), "/health/ready"), cancellationToken),
            ApiPort: 0),
        new(
            Name: "server",
            Project: "src/Simple.Server/Simple.Server.csproj",
            Count: _ => 1,
            // The API, and the endpoint onboard terminals connect to.
            PortsPerProcess: 2,
            Arguments: context =>
            [
                $"--urls={context.Url()}",
                $"--Simple:Instance={context.Instance}",
                $"--ConnectionStrings:Simple={context.SqlServer}",
                $"--Simple:OnboardTerminals:Url=https://127.0.0.1:{context.Ports[1].ToString(CultureInfo.InvariantCulture)}",
                $"--Simple:OnboardTerminals:ServerKeyName={ServerKeyName(context.Instance)}",
                $"--Simple:MesGateway:Url={context.UrlOf("mes-gateway")}",
            ],
            IsReady: (context, http, cancellationToken) =>
                Readiness.HttpOkAsync(http, new Uri(context.Url(), "/health/ready"), cancellationToken),
            ApiPort: 0),
        new(
            Name: "terminal",
            Project: "src/Simple.OnboardTerminal.Headless/Simple.OnboardTerminal.Headless.csproj",
            Count: options => options.Terminals,
            // The test input, through which tests and AI sessions scan and press buttons.
            PortsPerProcess: 1,
            Arguments: context =>
            [
                $"--config={Path.Combine(context.Directory, "terminal.json")}",
                $"--test-input={context.Url()}",
            ],
            IsReady: (context, http, cancellationToken) =>
                Readiness.HttpOkAsync(http, new Uri(context.Url(), "/test/ready"), cancellationToken),
            ApiPort: 0,
            Prepare: OnboardTerminalSettings.WriteAsync),
    ];

    /// <summary>The component that answers the server API (status, events).</summary>
    public static Component Server { get; } = All.Single(c => c.Name == "server");

    /// <summary>
    /// The persisted key behind the certificate the instance's terminals pin,
    /// in the current user's key store. It outlives down, so the pin stays the
    /// same from one up to the next; down --reset deletes it.
    /// </summary>
    public static string ServerKeyName(string instance) => $"Simple-Server-{instance}";

    /// <summary>The processes of a component: <c>name</c> for one, <c>name-1</c>, <c>name-2</c>… for several.</summary>
    public static IEnumerable<string> ProcessNames(Component component, int count)
    {
        ArgumentNullException.ThrowIfNull(component);
        return count == 1
            ? [component.Name]
            : Enumerable.Range(1, count).Select(i => $"{component.Name}-{i.ToString(CultureInfo.InvariantCulture)}");
    }
}

/// <param name="Name">How the component is called in the state file, in <c>status</c> and in <c>logs &lt;name&gt;</c>.</param>
/// <param name="Project">Its project, relative to the repository root.</param>
/// <param name="Count">How many processes of it run for the given <c>up</c> options.</param>
/// <param name="PortsPerProcess">Loopback ports each process is given (<see cref="ComponentContext.Ports"/>).</param>
/// <param name="Arguments">Its command line. Settings go here, never into the environment, so they show in the process list.</param>
/// <param name="IsReady">Asked repeatedly after start until it answers true.</param>
/// <param name="ApiPort">Which of its ports serves HTTP for <c>status</c>, or null.</param>
/// <param name="Prepare">
/// Runs before each of its processes starts, once every component before it
/// is ready: writes what the process reads, for example settings that come
/// from another component.
/// </param>
public sealed record Component(
    string Name,
    string Project,
    Func<UpOptions, int> Count,
    int PortsPerProcess,
    Func<ComponentContext, IReadOnlyList<string>> Arguments,
    Func<ComponentContext, HttpClient, CancellationToken, Task<bool>> IsReady,
    int? ApiPort,
    Func<ComponentContext, HttpClient, CancellationToken, Task>? Prepare = null);

/// <summary>What <c>up</c> was asked for.</summary>
public sealed record UpOptions(int Terminals);

/// <summary>What one process of a component is started with.</summary>
/// <param name="Index">1-based among the processes of its component.</param>
/// <param name="Ports">This process's own ports.</param>
/// <param name="PortsOf">Every process's ports by process name, for components that connect to another.</param>
/// <param name="Directory">This process's own directory for files it reads or writes, outside the clone; kept after down.</param>
public sealed record ComponentContext(
    string Instance,
    string SqlServer,
    int Index,
    IReadOnlyList<int> Ports,
    IReadOnlyDictionary<string, IReadOnlyList<int>> PortsOf,
    string Directory)
{
    public Uri Url(int port = 0) => new($"http://127.0.0.1:{Ports[port].ToString(CultureInfo.InvariantCulture)}");

    /// <summary>The loopback address of another process's port, for a component that connects to it.</summary>
    public Uri UrlOf(string process, int port = 0) =>
        new($"http://127.0.0.1:{PortsOf[process][port].ToString(CultureInfo.InvariantCulture)}");
}

public static class Readiness
{
    public static async Task<bool> HttpOkAsync(HttpClient http, Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        try
        {
            using var response = await http.GetAsync(uri, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false; // the request timed out
        }
    }
}
