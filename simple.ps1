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

& ([IO.Path]::ChangeExtension("$($built[-1])".Trim(), '.exe')) @args
exit $LASTEXITCODE
