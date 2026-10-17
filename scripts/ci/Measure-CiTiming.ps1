#Requires -Version 7
<#
.SYNOPSIS
    How long the ci workflow takes, phase by phase.

.DESCRIPTION
    Default: reads the last N finished runs of .github/workflows/ci.yml with
    `gh api` and prints, per phase, the median and the 90th percentile: over
    all runs, and split by whether another runner on the machine had a job
    running when the run started (the v2 line shares vm01).

    -Summary: used by the ci job itself. Writes this run's phase times to the
    job summary and records them, with the machine load the job saw, as a
    `ci-timing` notice annotation, which is where the default mode reads the
    machine load back from.

    Phases come from the job's step times in the GitHub API. Queue is from the
    job being queued to a runner picking it up; total is from queued to the
    job's end, which is what the 10-minute threshold of issue #28 compares.

.EXAMPLE
    pwsh -File scripts/ci/Measure-CiTiming.ps1 -Last 10

.EXAMPLE
    pwsh -File scripts/ci/Measure-CiTiming.ps1 -Last 30 -Branch main -Detail
#>
[CmdletBinding(DefaultParameterSetName = 'Report')]
param(
    [Parameter(ParameterSetName = 'Report')]
    [ValidateRange(1, 100)]
    [int] $Last = 10,

    [Parameter(ParameterSetName = 'Report')]
    [string] $Repository = 'trytoreachpeak0/simple',

    [Parameter(ParameterSetName = 'Report')]
    [string] $Branch,

    # Also list every run, not only the statistics.
    [Parameter(ParameterSetName = 'Report')]
    [switch] $Detail,

    [Parameter(ParameterSetName = 'Summary', Mandatory)]
    [switch] $Summary
)

$ErrorActionPreference = 'Stop'

# Must match the job and step names in .github/workflows/ci.yml.
$jobName = 'build-and-test'
$phases = [ordered]@{
    restore   = @{ Step = 'Restore (locked mode)'; Label = '恢复包' }
    lockFiles = @{ Step = 'Lock files up to date'; Label = '锁文件核对' }
    build     = @{ Step = 'Build'; Label = '编译' }
    test      = @{ Step = 'Test'; Label = '测试' }
}
$annotationTitle = 'ci-timing'

function Invoke-GitHubApi([string] $Path) {
    if ($env:GITHUB_ACTIONS -eq 'true') {
        $headers = @{ Authorization = "Bearer $env:GITHUB_TOKEN"; Accept = 'application/vnd.github+json' }
        return Invoke-RestMethod -Uri "$env:GITHUB_API_URL/$Path" -Headers $headers
    }
    $json = gh api $Path
    if ($LASTEXITCODE -ne 0) { throw "gh api $Path failed with exit code $LASTEXITCODE." }
    return $json | ConvertFrom-Json
}

function ConvertTo-UtcTime($Value) {
    if ($null -eq $Value) { return $null }
    if ($Value -is [datetime]) { return $Value.ToUniversalTime() }
    return [datetimeoffset]::Parse($Value, [cultureinfo]::InvariantCulture).UtcDateTime
}

function Get-Seconds($From, $To) {
    $start = ConvertTo-UtcTime $From
    $end = ConvertTo-UtcTime $To
    if ($null -eq $start -or $null -eq $end) { return $null }
    return [Math]::Round(($end - $start).TotalSeconds)
}

# Seconds per phase for one job object from the jobs API. A phase whose step
# did not run (skipped, or not finished yet) is $null.
function Get-JobTiming($Job) {
    $timing = [ordered]@{ queue = Get-Seconds $Job.created_at $Job.started_at }
    foreach ($key in $phases.Keys) {
        $step = $Job.steps | Where-Object name -EQ $phases[$key].Step | Select-Object -First 1
        $ran = $step -and $step.conclusion -ne 'skipped'
        $timing[$key] = $ran ? (Get-Seconds $step.started_at $step.completed_at) : $null
    }
    $timing.total = Get-Seconds $Job.created_at $Job.completed_at
    return $timing
}

function Format-Seconds($Seconds) {
    if ($null -eq $Seconds) { return '—' }
    $Seconds = [Math]::Round($Seconds)
    $span = [timespan]::FromSeconds($Seconds)
    return $span.TotalMinutes -ge 1 ? ('{0} 分 {1:00} 秒' -f [Math]::Floor($span.TotalMinutes), $span.Seconds) : "$Seconds 秒"
}

function Get-OtherRunnerJobCount {
    # The calling job is one Runner.Worker; any other one is another runner's job.
    return [Math]::Max(0, @(Get-Process -Name Runner.Worker -ErrorAction Ignore).Count - 1)
}

if ($Summary) {
    $jobs = Invoke-GitHubApi "repos/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID/attempts/$env:GITHUB_RUN_ATTEMPT/jobs"
    $job = $jobs.jobs | Where-Object name -EQ $jobName | Select-Object -First 1
    if (-not $job) { throw "Job '$jobName' not found in run $env:GITHUB_RUN_ID attempt $env:GITHUB_RUN_ATTEMPT." }
    $timing = Get-JobTiming $job
    $atStart = $env:SIMPLE_CI_OTHER_JOBS_AT_START
    $atEnd = Get-OtherRunnerJobCount
    $soFar = Get-Seconds $job.created_at ([datetime]::UtcNow)

    $cache = $env:NUGET_PACKAGES
    $cacheMB = (Test-Path -LiteralPath $cache) `
        ? [Math]::Round((Get-ChildItem -LiteralPath $cache -Recurse -File -Force | Measure-Object Length -Sum).Sum / 1MB) `
        : $null

    $lines = @(
        '## CI 各段耗时'
        ''
        '| 段 | 用时 |'
        '| --- | --- |'
        "| 排队（作业入队到 runner 领走） | $(Format-Seconds $timing.queue) |"
        foreach ($key in $phases.Keys) { "| $($phases[$key].Label) | $(Format-Seconds $timing[$key]) |" }
        "| 入队到本步骤 | $(Format-Seconds $soFar) |"
        ''
        "本机其他 runner 正在跑的作业：开始时 $atStart 个，结束时 $atEnd 个（vm01 上的其他 runner 都属于 v2 线）。"
        ''
        "NuGet 缓存：``$cache``，$($null -eq $cacheMB ? '不存在' : "$cacheMB MB")。"
    )
    $lines -join "`n" | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8

    # The job has not ended yet, so its total is not known here.
    $timing.Remove('total')
    $record = [ordered]@{ otherJobsAtStart = [int]$atStart; otherJobsAtEnd = $atEnd; nugetCacheMB = $cacheMB } + $timing
    Write-Output "::notice title=$annotationTitle::$($record | ConvertTo-Json -Compress)"
    return
}

# Report mode.
function Get-Percentile([double[]] $Values, [double] $Fraction) {
    # Nearest rank.
    $sorted = $Values | Sort-Object
    return $sorted[[Math]::Max(0, [Math]::Ceiling($Fraction * $sorted.Count) - 1)]
}

function Get-Median([double[]] $Values) {
    $sorted = @($Values | Sort-Object)
    $middle = [Math]::Floor($sorted.Count / 2)
    return $sorted.Count % 2 ? $sorted[$middle] : ($sorted[$middle - 1] + $sorted[$middle]) / 2
}

$query = "per_page=100&status=completed" + ($Branch ? "&branch=$([uri]::EscapeDataString($Branch))" : '')
$runs = (Invoke-GitHubApi "repos/$Repository/actions/workflows/ci.yml/runs?$query").workflow_runs |
    Where-Object conclusion -In 'success', 'failure' |
    Select-Object -First $Last

$rows = foreach ($run in $runs) {
    $job = (Invoke-GitHubApi "repos/$Repository/actions/runs/$($run.id)/jobs").jobs |
        Where-Object name -EQ $jobName | Select-Object -First 1
    if (-not $job -or $job.conclusion -eq 'skipped') { continue }
    $annotation = Invoke-GitHubApi "repos/$Repository/check-runs/$($job.id)/annotations" |
        Where-Object title -EQ $annotationTitle | Select-Object -First 1
    $busy = $annotation ? (($annotation.message | ConvertFrom-Json).otherJobsAtStart -gt 0) : $null
    [pscustomobject]([ordered]@{
            run        = $run.id
            event      = $run.event
            conclusion = $run.conclusion
            otherBusy  = $busy
        } + (Get-JobTiming $job))
}
$rows = @($rows)

if ($Detail) {
    $rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Output
}

$groups = [ordered]@{
    '全部'                 = $rows
    '开始时 v2 有作业在跑' = @($rows | Where-Object otherBusy -EQ $true)
    '开始时 v2 没有作业'   = @($rows | Where-Object otherBusy -EQ $false)
    '未记录机器负载'       = @($rows | Where-Object { $null -eq $_.otherBusy })
}
$columns = @(@{ Key = 'queue'; Label = '排队' }) +
    @($phases.Keys | ForEach-Object { @{ Key = $_; Label = $phases[$_].Label } }) +
    @(@{ Key = 'total'; Label = '全程' })

Write-Output "ci.yml 最近 $($rows.Count) 轮（$Repository$($Branch ? "，分支 $Branch" : '')；只计成功和失败的运行）"
foreach ($name in $groups.Keys) {
    $group = $groups[$name]
    if ($group.Count -eq 0) { continue }
    Write-Output ''
    Write-Output "$name（$($group.Count) 轮）"
    $stats = foreach ($column in $columns) {
        $values = @($group | ForEach-Object { $_.($column.Key) } | Where-Object { $null -ne $_ })
        [pscustomobject]@{
            段     = $column.Label
            轮数   = $values.Count
            中位数 = $values.Count ? (Format-Seconds (Get-Median $values)) : '—'
            P90    = $values.Count ? (Format-Seconds (Get-Percentile $values 0.9)) : '—'
        }
    }
    $stats | Format-Table -AutoSize | Out-String -Width 200 | Write-Output
}
