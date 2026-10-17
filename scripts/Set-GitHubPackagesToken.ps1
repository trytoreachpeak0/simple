#Requires -Version 7
<#
.SYNOPSIS
    Stores a GitHub Packages read token for the NuGet source named "github",
    as a credential only.

.DESCRIPTION
    nuget.config in this repository defines the source "github"
    (https://nuget.pkg.github.com/trytoreachpeak0/index.json). GitHub Packages
    wants a token even to read public packages, and none may enter the
    repository, so each machine keeps one in its user-level NuGet configuration.

    This script writes only <packageSourceCredentials><github>, never a
    <packageSources> entry. NuGet matches a credential to a source by name, so
    the credential applies to this repository's "github" source and to nothing
    else; repositories on the same machine without <clear /> do not start
    querying GitHub Packages (`dotnet nuget add source` would register the
    source user-wide, and a lapsed token would then break their restores).
    If the user-level file already registers a source named "github", the
    script removes that entry and says so.

    The token is read with Read-Host -MaskInput and never appears on a command
    line or in the PowerShell history; no dotnet process sees it.

    By default the password is encrypted for the current user with DPAPI, the
    way `dotnet nuget add source --password` stores it. Use -ClearText on a
    machine where restores run over SSH (simple01): an SSH key logon has no
    DPAPI master key, NuGet cannot decrypt the token there and the restore
    gets 401. In clear text any administrator process on that machine can read
    the token; it carries only read:packages.

    Run it again to replace the token.

.EXAMPLE
    pwsh -File scripts/Set-GitHubPackagesToken.ps1

.EXAMPLE
    pwsh -File scripts/Set-GitHubPackagesToken.ps1 -ClearText
#>
[CmdletBinding()]
param(
    [switch] $ClearText,

    [string] $UserName = 'trytoreachpeak0',

    # The user-level NuGet configuration by default.
    [string] $ConfigFile = (Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'NuGet' 'NuGet.Config'),

    # For tests: read the token from this variable's value instead of prompting.
    [Parameter(DontShow)]
    [string] $TokenFromEnvironment
)

$ErrorActionPreference = 'Stop'
$sourceName = 'github'

$token = $TokenFromEnvironment ? [Environment]::GetEnvironmentVariable($TokenFromEnvironment) : (Read-Host -Prompt 'Paste the read:packages token and press Enter' -MaskInput)
if ([string]::IsNullOrWhiteSpace($token)) { throw 'No token given.' }
$token = $token.Trim()

if ($ClearText) {
    $passwordKey = 'ClearTextPassword'
    $password = $token
}
else {
    # NuGet's own encryption for <add key="Password">: DPAPI, current user,
    # entropy "NuGet" (NuGet.Configuration EncryptionUtility).
    Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
    $protected = [System.Security.Cryptography.ProtectedData]::Protect(
        [System.Text.Encoding]::UTF8.GetBytes($token),
        [System.Text.Encoding]::UTF8.GetBytes('NuGet'),
        [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    $passwordKey = 'Password'
    $password = [Convert]::ToBase64String($protected)
}
Remove-Variable token

$directory = Split-Path -Parent $ConfigFile
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$xml = [xml]::new()
$xml.PreserveWhitespace = $false
if (Test-Path -LiteralPath $ConfigFile) {
    $xml.Load($ConfigFile)
}
else {
    $xml.LoadXml('<?xml version="1.0" encoding="utf-8"?><configuration />')
}
$configuration = $xml.DocumentElement

$registered = @($configuration.SelectNodes("packageSources/add[@key='$sourceName']"))
foreach ($node in $registered) {
    [void]$node.ParentNode.RemoveChild($node)
    Write-Output "Removed the user-level source registration '$sourceName' ($($node.GetAttribute('value'))); this repository's nuget.config defines it."
}

$credentials = $configuration.SelectSingleNode('packageSourceCredentials')
if (-not $credentials) { $credentials = $configuration.AppendChild($xml.CreateElement('packageSourceCredentials')) }
$existing = $credentials.SelectSingleNode($sourceName)
if ($existing) { [void]$credentials.RemoveChild($existing) }
$entry = $credentials.AppendChild($xml.CreateElement($sourceName))
foreach ($pair in @(@('Username', $UserName), @($passwordKey, $password))) {
    $add = $entry.AppendChild($xml.CreateElement('add'))
    $add.SetAttribute('key', $pair[0])
    $add.SetAttribute('value', $pair[1])
}

$settings = [System.Xml.XmlWriterSettings]::new()
$settings.Indent = $true
$settings.Encoding = [System.Text.UTF8Encoding]::new($false)
$writer = [System.Xml.XmlWriter]::Create($ConfigFile, $settings)
try { $xml.Save($writer) } finally { $writer.Dispose() }

Write-Output "Stored the $($ClearText ? 'clear-text' : 'DPAPI-encrypted') credential for source '$sourceName' in $ConfigFile (no source registered there)."
