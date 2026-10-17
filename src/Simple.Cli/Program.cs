using Simple.Cli;

if (!OperatingSystem.IsWindowsVersionAtLeast(6, 0, 6000))
{
    await Console.Error.WriteLineAsync("simple runs on Windows only.");
    return ExitCodes.Unexpected;
}

// Read through a pipe (a session, a script, simple remote's own output), the
// command line writes UTF-8: the console's code page (936 on a Chinese
// Windows) would garble every Chinese character, screen texts and test names
// among them. A console window keeps its code page.
if (Console.IsOutputRedirected)
{
    Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true });
}

if (Console.IsErrorRedirected)
{
    Console.SetError(new StreamWriter(Console.OpenStandardError(), new System.Text.UTF8Encoding(false)) { AutoFlush = true });
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
