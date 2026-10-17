#Requires -Version 7
# The simple command line, built from this clone and run with the given
# arguments: pwsh -File ./simple.ps1 up (or ./simple up from PowerShell).
# The build is quiet; its output is shown only when it fails (exit code 3,
# the command line's own code for a failed build).

$project = Join-Path $PSScriptRoot 'src/Simple.Cli/Simple.Cli.csproj'
$built = @(dotnet build $project -nologo -t:Build -getProperty:TargetPath 2>&1)
if ($LASTEXITCODE -ne 0) {
    $built | ForEach-Object { [Console]::Error.WriteLine($_) }
    [Console]::Error.WriteLine("Building $project failed.")
    exit 3
}

# Into a pipe the command line writes UTF-8 (Program.cs), but pwsh reads a
# native command's output in [Console]::OutputEncoding, the console's code
# page (936 on a Chinese Windows): a caller capturing `& ./simple.ps1 ... --json`
# would get its Chinese mangled. Read it as UTF-8 for this call only.
$encoding = [Console]::OutputEncoding
try {
    try { [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false) } catch { }
    & ([IO.Path]::ChangeExtension("$($built[-1])".Trim(), '.exe')) @args
    $exitCode = $LASTEXITCODE
}
finally {
    try { [Console]::OutputEncoding = $encoding } catch { }
}

exit $exitCode
