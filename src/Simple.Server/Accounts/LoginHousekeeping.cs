namespace Simple.Server.Accounts;

/// <summary>
/// Loads the accounts and logins before the server takes requests (after the
/// database is migrated), then flushes <see cref="LoginRegistry"/> every
/// <see cref="Period"/> and once more on the way down.
/// </summary>
public sealed class LoginHousekeeping(LoginRegistry registry, TimeProvider time) : IHostedLifecycleService, IDisposable
{
    /// <summary>
    /// How often use is written and the database read back: also how soon a
    /// password set by the server command ends the logins of a running server.
    /// </summary>
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(10);

    private readonly CancellationTokenSource stopping = new();
    private readonly Lock gate = new();
    private Task? loop;
    private Task? stopped;

    public Task StartingAsync(CancellationToken cancellationToken) => registry.LoadAsync(cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            loop = RunAsync(stopping.Token);
        }

        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            // The host can be stopped from two places at once (its run loop and
            // its owner); both wait for the one last flush.
            return loop is null ? Task.CompletedTask : stopped ??= StopLoopAsync(loop, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => stopping.Dispose();

    private async Task StopLoopAsync(Task running, CancellationToken cancellationToken)
    {
        await stopping.CancelAsync();
        try
        {
            await running;
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }

        await registry.FlushAsync(cancellationToken);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Period, time);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await registry.FlushAsync(cancellationToken);
        }
    }
}
