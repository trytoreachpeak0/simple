using Simple.Cli;

if (!OperatingSystem.IsWindowsVersionAtLeast(6, 0, 6000))
{
    await Console.Error.WriteLineAsync("simple runs on Windows only.");
    return ExitCodes.Unexpected;
}

return await SimpleCommand.InvokeAsync(args, CliEnvironment.FromProcess());
