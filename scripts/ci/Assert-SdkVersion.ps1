#Requires -Version 7
<#
.SYNOPSIS
    Fails unless the .NET SDK resolved in this repository is in the feature
    band that global.json pins and not older than the pinned version.

.DESCRIPTION
    global.json pins an SDK with rollForward latestPatch (issue #43): any newer
    patch of the same feature band is accepted, because Microsoft Update
    replaces an installed patch with the next one on its own. A feature band is
    the major and minor version plus the hundreds of the patch number:
    10.0.303 and 10.0.315 share band 10.0.3xx, 10.0.400 does not.

    Judged by exit codes and by comparing version numbers only, never by the
    text dotnet prints: vm01 runs a Chinese-locale Windows. When no installed
    SDK satisfies global.json, `dotnet --version` exits non-zero and this
    script fails with that exit code in its message.

    Exit code 0: the resolved SDK is acceptable. Exit code 1: it is not, or it
    cannot be judged (dotnet failed, or printed something that is not a single
    major.minor.patch version).

    -Resolved skips running dotnet and judges the given version instead, so
    the comparison can be checked without installing an SDK.

.EXAMPLE
    pwsh -File scripts/ci/Assert-SdkVersion.ps1

.EXAMPLE
    pwsh -File scripts/ci/Assert-SdkVersion.ps1 -Resolved 10.0.400
#>
param(
    [string] $GlobalJson = (Join-Path $PSScriptRoot '../../global.json'),

    [string] $Resolved
)

$ErrorActionPreference = 'Stop'
# A failing dotnet is judged below by its exit code, not turned into an error.
$PSNativeCommandUseErrorActionPreference = $false

function Get-SdkVersion([string] $Text) {
    # ASCII digits only (\d also matches other scripts' digits, which [version]
    # then fails on), and \z, not $, which also matches before a final newline.
    if ($Text -notmatch '\A[0-9]+\.[0-9]+\.[0-9]+\z') { return $null }
    [version] $Text
}

$GlobalJson = (Resolve-Path $GlobalJson).Path
$pinnedText = (Get-Content $GlobalJson -Raw | ConvertFrom-Json).sdk.version
$pinned = Get-SdkVersion $pinnedText
if (-not $pinned) {
    Write-Output "::error::global.json pins SDK '$pinnedText', which is not a major.minor.patch version."
    exit 1
}

if (-not $PSBoundParameters.ContainsKey('Resolved')) {
    # dotnet looks for global.json from its working directory upwards.
    Push-Location (Split-Path $GlobalJson)
    try {
        $output = @(dotnet --version)
        $code = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    if ($code -ne 0) {
        dotnet --list-sdks
        Write-Output "::error::No installed SDK satisfies global.json (SDK $pinnedText, rollForward latestPatch): dotnet --version exited with code $code. Install the latest SDK $($pinned.Major).$($pinned.Minor).$([Math]::Floor($pinned.Build / 100))xx, at least $pinnedText, on this machine."
        exit 1
    }
    $Resolved = $output.Count -eq 1 ? $output[0].Trim() : ($output -join ' ')
}

$version = Get-SdkVersion $Resolved
$sameBand = $version -and
    $version.Major -eq $pinned.Major -and
    $version.Minor -eq $pinned.Minor -and
    [Math]::Floor($version.Build / 100) -eq [Math]::Floor($pinned.Build / 100)

if (-not $sameBand -or $version -lt $pinned) {
    Write-Output "::error::global.json pins SDK $pinnedText (rollForward latestPatch: the same feature band, this patch or newer); this machine resolves '$Resolved'."
    exit 1
}

Write-Output "SDK $Resolved (global.json pins $pinnedText, rollForward latestPatch)"
