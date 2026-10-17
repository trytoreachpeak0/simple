#Requires -Version 7
# The remote side of `simple remote` (src/Simple.Cli/Remote/RemoteRun.cs).
# The command line copies this file to <root>/bin/agent-<hash>.ps1 on the
# remote machine and runs it with one request, a base64 JSON object. It does
# only what needs to happen on that machine in one process: take the
# instance's lock (and a slot for heavy commands), bring the snapshot into
# the shared bare repository, check it out in the instance's worktree and
# run the command there. Which command to run is decided by the command line.
#
# The lock and the slot are files held open exclusively, so they are released
# when this process ends, also when the SSH session is cut and the session's
# processes are killed. The last line on stderr is "##simple-remote-<nonce>"
# followed by a JSON result; everything else is passed through as is.

param([Parameter(Mandatory)][string] $Request)

$ErrorActionPreference = 'Stop'
# Progress records would reach the command line as CLIXML on stderr.
$ProgressPreference = 'SilentlyContinue'
# The session's console, which the command shares, writes UTF-8, which is how
# the command line reads it. In the machine's code page (936 on a Chinese
# Windows) every Chinese character would arrive garbled.
try {
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
}
catch [IO.IOException] {
    # No console to set: then nothing shares one either.
}

# The file is named after the hash of its content. A copy that does not match
# (cut short, edited by hand) does not run; the line ##simple-remote-agent-missing
# makes the command line copy it again, as when it is missing (RemoteRun.AgentMissingLine).
$named = [IO.Path]::GetFileNameWithoutExtension($PSCommandPath) -replace '^agent-', ''
$actual = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant().Substring(0, 12)
if ($actual -ne $named) {
    [Console]::Error.WriteLine("simple remote: $PSCommandPath has content hash $actual, not $named; not running it.")
    [Console]::Error.WriteLine('##simple-remote-agent-missing')
    exit 1
}

# Marks this agent as used at once, before any wait: an agent no run has
# started for a week is removed (Remove-OldFiles). Best effort.
try {
    [IO.File]::SetLastWriteTimeUtc($PSCommandPath, [DateTime]::UtcNow)
}
catch [IO.IOException], [UnauthorizedAccessException] {
}

$spec =[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Request)) | ConvertFrom-Json -AsHashtable
$root = Split-Path -Parent $PSScriptRoot
$repo = Join-Path $root 'repo.git'
$instance = $spec.instance
$worktree = Join-Path $root "wt/$instance"

function Say([string] $text) {
    [Console]::Error.WriteLine("simple remote: $text")
}

# Exits the process outright: the reader below may be blocked on the input,
# and nothing else is left to do.
function Finish([hashtable] $result) {
    [Console]::Error.WriteLine("##simple-remote-$($spec.nonce) $($result | ConvertTo-Json -Compress -Depth 5)")
    [Environment]::Exit(0)
}

# The laptop's end of the connection. The command line writes a heartbeat
# line into this session's input every few seconds: a killed ssh or Ctrl+C
# ends the input, a lost network silences it. sshd ends neither the session
# nor its processes on its own (simple01, 2026-10-17), so this does. Reading
# the input blocks with no timeout (ReadLineAsync too), so a
# reader of its own notes when each line came, and every wait below checks.
$alive = [hashtable]::Synchronized(@{ Last = [DateTime]::UtcNow; Ended = $false })
$reader = [PowerShell]::Create()
[void]$reader.AddScript({
        param($alive)
        # A reader of its own on the raw input, not [Console]::In: that one is
        # shared and locked while a read waits, and starting a native command
        # (git) waits for that lock, so a silent connection would hang the run.
        $heartbeats = [IO.StreamReader]::new([Console]::OpenStandardInput())
        while ($null -ne $heartbeats.ReadLine()) {
            $alive.Last = [DateTime]::UtcNow
        }

        $alive.Ended = $true
    }).AddArgument($alive)
[void]$reader.BeginInvoke()

function Test-Abandoned {
    $alive.Ended -or ($spec.aliveSeconds -and ([DateTime]::UtcNow - $alive.Last).TotalSeconds -gt $spec.aliveSeconds)
}

# Ends the run: the command with every process it started (broken-away
# components of simple up are its descendants too), then this process,
# which frees the instance's lock and the slot.
function Stop-Abandoned([Diagnostics.Process] $process) {
    if ($process) {
        try { $process.Kill($true) } catch { }
    }

    # Nothing is written on the way out: with this end gone, a write into
    # the session's output may block, and no one would read it.
    [Environment]::Exit(3)
}

# Runs git and returns its exit code and output. A process of its own with
# an empty input: git started with & and no input blocks while the heartbeat
# reader waits on this session's input (a silent connection hung git init,
# 2026-10-17).
#
# -Interruptible: the connection is watched while git runs, as for the
# command, and a cut ends git too. Only for fetches, from the origin or of a
# bundle, which can take minutes and leave at worst unused objects and the
# lock of a ref only this instance writes (cleared under the instance's lock,
# Receive-Snapshot). Every other
# git runs to its end and the connection is checked after it: a killed
# worktree add or checkout leaves the worktree locked with an index.lock,
# and every later run of the instance failed (review of #64).
function Start-Git([string[]] $arguments, [switch] $Interruptible) {
    $start = [Diagnostics.ProcessStartInfo]::new('git')
    foreach ($argument in $arguments) {
        $start.ArgumentList.Add($argument)
    }

    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    $process = [Diagnostics.Process]::Start($start)
    $process.StandardInput.Close()
    $output = $process.StandardOutput.ReadToEndAsync()
    $errors = $process.StandardError.ReadToEndAsync()
    while (-not $process.WaitForExit(500)) {
        if ($Interruptible -and (Test-Abandoned)) {
            Stop-Abandoned $process
        }
    }

    $process.WaitForExit()
    if (Test-Abandoned) {
        Stop-Abandoned $null
    }

    @{ ExitCode = $process.ExitCode; Output = "$($output.Result)$($errors.Result)".Trim() }
}

# git fetch ends with gc --auto or maintenance, in the foreground on Windows;
# killed there, it may leave the shared cache's packed-refs.lock. So fetches
# run neither, and Receive-Snapshot runs gc --auto on its own, to its end.
$fetchOptions = @('-c', 'gc.auto=0', '-c', 'maintenance.auto=false')

function Invoke-Git {
    $result = Start-Git $args
    if ($result.ExitCode -ne 0) {
        throw "git $($args -join ' ') exited with $($result.ExitCode): $($result.Output -replace '\s+', ' ')"
    }
}

function Test-Commit([string] $commit) {
    (Start-Git @('-C', $repo, 'cat-file', '-e', "$commit^{commit}")).ExitCode -eq 0
}

function Get-Holder([string] $lock) {
    try { [IO.File]::ReadAllText("$lock.holder") } catch { 'unknown' }
}

# Opens $lock exclusively and records who holds it; $null when it is taken.
function Open-Lock([string] $lock) {
    try {
        $stream = [IO.File]::Open($lock, 'OpenOrCreate', 'ReadWrite', 'None')
    }
    catch [IO.IOException] {
        return $null
    }

    $since = [DateTimeOffset]::Now.ToString('yyyy-MM-dd HH:mm:ss')
    [IO.File]::WriteAllText("$lock.holder", "$($spec.label) (pid $PID, since $since)")
    $stream
}

# Waits until one of $locks is free, saying whom it waits for. $null after the deadline.
function Wait-Lock([string[]] $locks, [string] $what) {
    $deadline = [DateTime]::UtcNow.AddSeconds($spec.waitSeconds)
    $said = $null
    while ($true) {
        foreach ($lock in $locks) {
            if ($stream = Open-Lock $lock) {
                return $stream
            }
        }

        $holders = ($locks | ForEach-Object { Get-Holder $_ }) -join '; '
        if ($holders -ne $said) {
            Say "waiting for $what, held by $holders"
            $said = $holders
        }

        if ([DateTime]::UtcNow -gt $deadline) {
            Finish @{ status = 'busy'; error = "Waited $($spec.waitSeconds) s for $what, held by $holders." }
        }

        if (Test-Abandoned) {
            Stop-Abandoned $null
        }

        Start-Sleep -Milliseconds 500
    }
}

function Fetch-Origin {
    if (-not $spec.originUrl) {
        return
    }

    # Into the instance's own ref (Snapshot.OriginRef), not one all instances
    # share: a cut leaves that ref's lock, and a lock on a shared ref no run
    # may remove (another instance may be fetching into it) stopped every
    # later fetch with "Another git process seems to be running" (review of #64).
    Say "fetching $($spec.originBranch) from $($spec.originUrl)"
    $result = Start-Git @($fetchOptions + @('-C', $repo, 'fetch', '--quiet', $spec.originUrl, "+refs/heads/$($spec.originBranch):$($spec.originRef)")) -Interruptible
    $result.Output -split '\r?\n' | Where-Object { $_ } | ForEach-Object { Say $_ }
}

# Reads the bundle only, so a cut may end it.
function Test-Bundle([string] $bundle) {
    (Start-Git @('-C', $repo, 'bundle', 'verify', '--quiet', $bundle) -Interruptible).ExitCode -eq 0
}

# A fetch into one of the instance's refs ended by a cut leaves its lock;
# only this instance's runs write those refs, under the instance's lock,
# which the caller holds.
function Clear-RefLocks {
    foreach ($ref in $spec.ref, $spec.originRef) {
        Remove-Item -LiteralPath (Join-Path $repo "$ref.lock") -Force -ErrorAction Ignore
    }
}

function Receive-Snapshot {
    Clear-RefLocks
    $fetched = $false
    if ($spec.bundle) {
        $bundle = Join-Path $root $spec.bundle
        try {
            if (-not (Test-Bundle $bundle)) {
                # Most likely the cache lacks what the bundle builds on.
                Fetch-Origin
                $fetched = $true
                if (-not (Test-Bundle $bundle)) {
                    # Finish exits outright and skips the finally below.
                    Remove-Item -LiteralPath $bundle -Force -ErrorAction Ignore
                    Finish @{ status = 'needFullBundle' }
                }
            }

            $result = Start-Git @($fetchOptions + @('-C', $repo, 'fetch', '--quiet', $bundle, "+$($spec.ref):$($spec.ref)")) -Interruptible
            if ($result.ExitCode -ne 0) {
                throw "git fetch of $($spec.bundle) exited with $($result.ExitCode): $($result.Output -replace '\s+', ' ')"
            }

            $fetched = $true
        }
        finally {
            Remove-Item -LiteralPath $bundle -Force -ErrorAction Ignore
        }
    }
    elseif (-not (Test-Commit $spec.commit)) {
        # Nothing was sent: the snapshot is already on the origin.
        Fetch-Origin
        $fetched = $true
    }

    if (-not (Test-Commit $spec.commit)) {
        Finish @{ status = 'needFullBundle' }
    }

    # Keeps the snapshot from being collected while the instance uses it.
    Invoke-Git -C $repo update-ref $spec.ref $spec.commit
    if ($fetched) {
        # What the fetches leave out ($fetchOptions), to its end; a failure
        # here is git's own business and not the run's.
        [void](Start-Git @('-C', $repo, 'gc', '--auto', '--quiet'))
    }
}

# A git killed in the instance's worktree (by an agent before #64's review, or
# on a machine that lost power) leaves its index.lock, and a worktree add
# killed early leaves the worktree locked "initializing": every later
# checkout, add or remove of the instance then fails. Only this instance's
# runs touch its worktree, under the lock held here, so both are stale.
function Repair-Worktree {
    $gitFile = [IO.Path]::GetFullPath((Join-Path $worktree '.git'))
    foreach ($admin in Get-ChildItem -LiteralPath (Join-Path $repo 'worktrees') -Directory -ErrorAction Ignore) {
        $gitdir = Join-Path $admin.FullName 'gitdir'
        if (-not (Test-Path -LiteralPath $gitdir) -or [IO.Path]::GetFullPath((Get-Content -LiteralPath $gitdir -Raw).Trim()) -ne $gitFile) {
            continue
        }

        Remove-Item -LiteralPath (Join-Path $admin.FullName 'index.lock') -Force -ErrorAction Ignore
        $locked = Join-Path $admin.FullName 'locked'
        if ((Test-Path -LiteralPath $locked) -and (Get-Content -LiteralPath $locked -Raw) -match '\Ainitializing') {
            # What git worktree unlock does.
            Remove-Item -LiteralPath $locked -Force
        }
    }
}

function Checkout-Snapshot {
    Repair-Worktree
    if (Test-Path -LiteralPath (Join-Path $worktree '.git')) {
        # --force and clean: exactly the snapshot, whatever a build changed or
        # left. Ignored files (bin, obj, artifacts) stay, so builds are incremental.
        Invoke-Git -C $worktree checkout --quiet --force --detach $spec.commit
        Invoke-Git -C $worktree clean -fdq
    }
    else {
        if (Test-Path -LiteralPath $worktree) {
            Remove-Item -LiteralPath $worktree -Recurse -Force
        }

        # --force also takes over a worktree that is registered but whose
        # directory is gone. No prune: it could drop the registration of a
        # worktree another instance is adding at this moment.
        Invoke-Git -C $repo worktree add --quiet --force --detach $worktree $spec.commit
    }
}

function Remove-Instance {
    if (Test-Path -LiteralPath $worktree) {
        Repair-Worktree
        # Twice --force: also a worktree that is locked.
        [void](Start-Git @('-C', $repo, 'worktree', 'remove', '--force', '--force', $worktree))
        if (Test-Path -LiteralPath $worktree) {
            Remove-Item -LiteralPath $worktree -Recurse -Force
        }
    }

    Remove-Item -LiteralPath (Join-Path $root "runs/$instance") -Recurse -Force -ErrorAction Ignore
    # forget checks nothing out, so Receive-Snapshot did not clear them: a
    # lock left there would make update-ref -d fail and keep the ref.
    Clear-RefLocks
    foreach ($ref in $spec.ref, $spec.originRef) {
        [void](Start-Git @('-C', $repo, 'update-ref', '-d', $ref))
    }
}

# Files no run needs any more. Every version of the command line installs an
# agent of its own (named after its content) and nothing removed the old ones:
# an agent no run has started for $spec.keepAgentDays is removed, and a clone
# still on that version copies it again. A bundle is read within the run that
# sent it; one a day old is a run's that ended before it could remove it.
function Remove-OldFiles {
    try {
        $agents = [DateTime]::UtcNow.AddDays(-$spec.keepAgentDays)
        Get-ChildItem -LiteralPath $PSScriptRoot -Filter 'agent-*.ps1' -File |
            Where-Object { $_.Name -match '\Aagent-[0-9a-f]{12}\.ps1\z' -and $_.FullName -ne $PSCommandPath -and $_.LastWriteTimeUtc -lt $agents } |
            Remove-Item -Force -ErrorAction Ignore
        $bundles = [DateTime]::UtcNow.AddDays(-1)
        Get-ChildItem -LiteralPath (Join-Path $root 'incoming') -Filter '*.bundle' -File |
            Where-Object { $_.LastWriteTimeUtc -lt $bundles } |
            Remove-Item -Force -ErrorAction Ignore
    }
    catch {
        Say "could not remove old agents and bundles: $_"
    }
}

# The names of the keys in one store of the Microsoft Software Key Storage
# Provider: .NET opens a key by name but cannot list them.
function Get-KeyNames([bool] $machine) {
    if (-not ('SimpleRemoteKeyNames' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class SimpleRemoteKeyNames
{
    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptOpenStorageProvider(out IntPtr provider, string name, int flags);

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptEnumKeys(IntPtr provider, string scope, out IntPtr keyName, ref IntPtr state, int flags);

    [DllImport("ncrypt.dll")]
    private static extern int NCryptFreeBuffer(IntPtr buffer);

    [DllImport("ncrypt.dll")]
    private static extern int NCryptFreeObject(IntPtr handle);

    public static string[] List(bool machine)
    {
        IntPtr provider;
        int status = NCryptOpenStorageProvider(out provider, "Microsoft Software Key Storage Provider", 0);
        if (status != 0) throw new Win32Exception(status);
        var names = new List<string>();
        IntPtr state = IntPtr.Zero;
        try
        {
            while (true)
            {
                IntPtr key;
                // NCRYPT_MACHINE_KEY_FLAG, NCRYPT_SILENT_FLAG; NTE_NO_MORE_ITEMS ends the list.
                status = NCryptEnumKeys(provider, null, out key, ref state, (machine ? 0x20 : 0) | 0x40);
                if (status == unchecked((int)0x8009002A)) break;
                if (status != 0) throw new Win32Exception(status);
                names.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(key)));
                NCryptFreeBuffer(key);
            }
        }
        finally
        {
            if (state != IntPtr.Zero) NCryptFreeBuffer(state);
            NCryptFreeObject(provider);
        }
        return names.ToArray();
    }
}
'@
    }

    [SimpleRemoteKeyNames]::List($machine)
}

# Whether $name, read with $pattern, names an instance that has a worktree here.
function Test-SetUp([string] $name, [string] $pattern) {
    $match = [regex]::Match($name, $pattern)
    $match.Success -and (Test-Path -LiteralPath (Join-Path $root "wt/$($match.Groups[1].Value)"))
}

# Test databases and keys that runs ended before their tests could clean up
# left behind (a cut connection ends a run with its tests), cleared before
# heavy work. Only while every slot is free: a test run holds one for as long
# as it runs, so nothing cleared is in use; with any slot taken, a later run
# clears them. Only names matching the request's patterns, the tests' own
# naming, are touched, and not those of an instance set up here: a clone may
# be named like a test instance, and the system simple remote up started for
# it holds no slot. A failure is said and the run goes on.
function Clear-Leftovers {
    $sweep = $spec.sweep
    $held = [Collections.Generic.List[IO.FileStream]]::new()
    try {
        # Every slot this machine has had, not only as many as this client
        # counts: a client of another version may count more.
        $slots = @(0..($spec.slots - 1) | ForEach-Object { Join-Path $root "slots/$_.lock" }) +
            @(Get-ChildItem -LiteralPath (Join-Path $root 'slots') -Filter '*.lock' -File | ForEach-Object FullName)
        foreach ($slot in $slots | ForEach-Object { [IO.Path]::GetFullPath($_) } | Sort-Object -Unique) {
            $stream = Open-Lock $slot
            if (-not $stream) {
                return
            }

            $held.Add($stream)
        }

        if ($sweep.sqlServer) {
            $connection = [System.Data.SqlClient.SqlConnection]::new($sweep.sqlServer)
            $connection.Open()
            try {
                $command = $connection.CreateCommand()
                $command.CommandText = 'SELECT name FROM sys.databases'
                $reader = $command.ExecuteReader()
                $names = @(while ($reader.Read()) { $reader.GetString(0) })
                $reader.Dispose()
                foreach ($name in $names | Where-Object { [regex]::IsMatch($_, $sweep.databases) -and -not (Test-SetUp $_ '\ASimple_(.+)\z') }) {
                    $drop = $connection.CreateCommand()
                    $drop.CommandText = "DECLARE @sql nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@name) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(@name); EXEC (@sql);"
                    [void]$drop.Parameters.AddWithValue('@name', $name)
                    [void]$drop.ExecuteNonQuery()
                    Say "dropped database $name, left behind by an earlier test run"
                }
            }
            finally {
                $connection.Dispose()
            }
        }

        $machine = $sweep.keyStore -eq 'machine'
        $options = $machine ? [Security.Cryptography.CngKeyOpenOptions]::MachineKey : [Security.Cryptography.CngKeyOpenOptions]::None
        foreach ($name in Get-KeyNames $machine | Where-Object { [regex]::IsMatch($_, $sweep.keys) -and -not (Test-SetUp $_ '\ASimple-Server-(.+)\z') }) {
            [Security.Cryptography.CngKey]::Open($name, [Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider, $options).Delete()
            Say "deleted key $name, left behind by an earlier test run"
        }
    }
    catch {
        Say "could not clear what earlier test runs left behind: $_"
    }
    finally {
        foreach ($stream in $held) {
            $stream.Dispose()
        }
    }
}

# Keeps the newest runs of the instance.
function Remove-OldRuns {
    $runs = Join-Path $root "runs/$instance"
    if (Test-Path -LiteralPath $runs) {
        Get-ChildItem -LiteralPath $runs -Directory | Sort-Object Name -Descending | Select-Object -Skip $spec.keepRuns |
            Remove-Item -Recurse -Force -ErrorAction Ignore
    }
}

try {
    foreach ($directory in 'wt', 'runs', 'slots', 'locks', 'incoming') {
        New-Item -ItemType Directory -Force -Path (Join-Path $root $directory) | Out-Null
    }

    if (-not (Test-Path -LiteralPath (Join-Path $repo 'HEAD'))) {
        # Two first runs at once must not both create it.
        $creating = Wait-Lock @(Join-Path $root 'locks/repo.lock') 'the repository cache'
        if (-not (Test-Path -LiteralPath (Join-Path $repo 'HEAD'))) {
            Invoke-Git init --quiet --bare $repo
        }

        $creating.Dispose()
    }

    if ($spec.operation -eq 'probe') {
        Finish @{ status = 'ok'; hasCommit = (-not $spec.commit) -or (Test-Commit $spec.commit) }
    }

    $lock = Wait-Lock @(Join-Path $root "locks/$instance.lock") "instance $instance"
    Remove-OldFiles

    if ($spec.commit) {
        Receive-Snapshot
        Checkout-Snapshot
    }

    $exitCode = 0
    $ran = $false
    $hasResults = $false
    if ($spec.fileName) {
        if (-not (Test-Path -LiteralPath (Join-Path $worktree '.git'))) {
            if ($spec.operation -ne 'forget') {
                Finish @{ status = 'noWorktree' }
            }
        }
        else {
            $slot = $null
            if ($spec.slot) {
                if ($spec.sweep) {
                    Clear-Leftovers
                }

                $slots =0..($spec.slots - 1) | ForEach-Object { Join-Path $root "slots/$_.lock" }
                $slot = Wait-Lock $slots 'a slot for heavy work'
            }

            $start = [Diagnostics.ProcessStartInfo]::new($spec.fileName, $spec.arguments)
            $start.UseShellExecute = $false
            $start.WorkingDirectory = $worktree
            foreach ($name in $spec.environment.Keys) {
                $start.Environment[$name] = $spec.environment[$name]
            }

            if ($spec.run) {
                $run = Join-Path $root "runs/$instance/$($spec.run)"
                New-Item -ItemType Directory -Force -Path $run | Out-Null
                $start.Environment['SIMPLE_REMOTE_RUN'] = $run
            }

            # Not redirected: the command writes straight into this process's
            # stdout and stderr, which are the SSH channel.
            # Its own, empty input: the heartbeats are this process's.
            $start.RedirectStandardInput = $true
            $process = [Diagnostics.Process]::Start($start)
            $process.StandardInput.Close()
            while (-not $process.WaitForExit(500)) {
                if (Test-Abandoned) {
                    Stop-Abandoned $process
                }
            }

            $process.WaitForExit()
            $exitCode = $process.ExitCode
            $ran = $true
            if ($slot) {
                $slot.Dispose()
            }

            if ($spec.run) {
                $hasResults = [bool](Get-ChildItem -LiteralPath $run -Recurse -File | Select-Object -First 1)
                Remove-OldRuns
            }
        }
    }

    if ($spec.operation -eq 'forget' -and $exitCode -eq 0) {
        Remove-Instance
    }

    Finish @{ status = 'ran'; ran = $ran; exitCode = $exitCode; hasResults = $hasResults }
}
catch {
    Finish @{ status = 'failed'; error = "$_" }
}
