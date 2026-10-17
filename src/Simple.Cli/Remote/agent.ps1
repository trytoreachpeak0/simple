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

# The file is named after the hash of its content. A copy that does not match
# (cut short, edited by hand) does not run; 199 makes the command line copy it
# again, as when it is missing.
$named = [IO.Path]::GetFileNameWithoutExtension($PSCommandPath) -replace '^agent-', ''
$actual = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant().Substring(0, 12)
if ($actual -ne $named) {
    [Console]::Error.WriteLine("simple remote: $PSCommandPath has content hash $actual, not $named; not running it.")
    exit 199
}
$spec = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Request)) | ConvertFrom-Json -AsHashtable
$root = Split-Path -Parent $PSScriptRoot
$repo = Join-Path $root 'repo.git'
$instance = $spec.instance
$worktree = Join-Path $root "wt/$instance"

function Say([string] $text) {
    [Console]::Error.WriteLine("simple remote: $text")
}

function Finish([hashtable] $result) {
    [Console]::Error.WriteLine("##simple-remote-$($spec.nonce) $($result | ConvertTo-Json -Compress -Depth 5)")
    exit 0
}

function Invoke-Git {
    $output = & git @args 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "git $($args -join ' ') exited with $LASTEXITCODE`: $($output -join ' ')"
    }
}

function Test-Commit([string] $commit) {
    & git -C $repo cat-file -e "$commit^{commit}" 2>$null
    $LASTEXITCODE -eq 0
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

        Start-Sleep -Milliseconds 500
    }
}

function Fetch-Origin {
    if (-not $spec.originUrl) {
        return
    }

    Say "fetching $($spec.originBranch) from $($spec.originUrl)"
    & git -C $repo fetch --quiet $spec.originUrl "+refs/heads/$($spec.originBranch):refs/remotes/origin/$($spec.originBranch)" 2>&1 |
        ForEach-Object { Say "$_" }
}

function Receive-Snapshot {
    if ($spec.bundle) {
        $bundle = Join-Path $root $spec.bundle
        try {
            & git -C $repo bundle verify --quiet $bundle 2>$null
            if ($LASTEXITCODE -ne 0) {
                # Most likely the cache lacks what the bundle builds on.
                Fetch-Origin
                & git -C $repo bundle verify --quiet $bundle 2>$null
                if ($LASTEXITCODE -ne 0) {
                    Finish @{ status = 'needFullBundle' }
                }
            }

            Invoke-Git -C $repo fetch --quiet $bundle "+$($spec.ref):$($spec.ref)"
        }
        finally {
            Remove-Item -LiteralPath $bundle -Force -ErrorAction Ignore
        }
    }
    elseif (-not (Test-Commit $spec.commit)) {
        # Nothing was sent: the snapshot is already on the origin.
        Fetch-Origin
    }

    if (-not (Test-Commit $spec.commit)) {
        Finish @{ status = 'needFullBundle' }
    }

    # Keeps the snapshot from being collected while the instance uses it.
    Invoke-Git -C $repo update-ref $spec.ref $spec.commit
}

function Checkout-Snapshot {
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
        & git -C $repo worktree remove --force $worktree 2>$null
        if (Test-Path -LiteralPath $worktree) {
            Remove-Item -LiteralPath $worktree -Recurse -Force
        }
    }

    Remove-Item -LiteralPath (Join-Path $root "runs/$instance") -Recurse -Force -ErrorAction Ignore
    & git -C $repo update-ref -d $spec.ref 2>$null
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
                $slots = 0..($spec.slots - 1) | ForEach-Object { Join-Path $root "slots/$_.lock" }
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
            $process = [Diagnostics.Process]::Start($start)
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
