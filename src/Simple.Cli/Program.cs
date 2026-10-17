using Simple.Cli;

if (!OperatingSystem.IsWindowsVersionAtLeast(6, 0, 6000))
{
    await Console.Error.WriteLineAsync("simple runs on Windows only.");
    return ExitCodes.Unexpected;
}

CliEnvironment environment;
try
{
    environment = CliEnvironment.FromProcess();
}
catch (InvalidOperationException e)
{
    // Run from a build outside a SIMPLE clone: report it like any other bad setting.
    return Outcome.Fail(ExitCodes.Usage, e.Message).Write(args.Contains(SimpleCommand.JsonOption), Console.Out, Console.Error);
}

return await SimpleCommand.InvokeAsync(args, environment);
