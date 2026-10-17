#Requires -Version 7
<#
.SYNOPSIS
Runs the #18 test steps against the published probe on this machine over
loopback ("step 0" of the ticket). Never touches another machine.

.DESCRIPTION
Starts the server, clients and the linkcut relay as child processes, drives
them through their standard input, and writes one evidence file per step
(log excerpts plus measured numbers) under <Run>/results. Run data (server
certificate, terminal registry, client identities) lives under <Run>, outside
the repository. Client keys are created in the current user's CNG store and
CurrentUser\My; step 'cleanup' removes them and shows that they are gone.

.EXAMPLE
pwsh -File tools/protocol-probe/loopback/Run-Loopback.ps1 -Run F:/simple/probe-run/18 -Bin artifacts/probe -Steps 1,8
#>
param(
    [Parameter(Mandatory)] [string] $Run,
    [Parameter(Mandatory)] [string] $Bin,
    [Parameter(Mandatory)] [string[]] $Steps,
    [int] $SoakMinutes = 30
)

$ErrorActionPreference = 'Stop'
$Steps = $Steps -split ','
$Run = [IO.Path]::GetFullPath($Run)
$Bin = [IO.Path]::GetFullPath($Bin)
$ServerExe = Join-Path $Bin 'server/simple-probe-server.exe'
$ClientExe = Join-Path $Bin 'client/simple-probe-client.exe'
$LinkCutExe = Join-Path $Bin 'linkcut/simple-probe-linkcut.exe'
$Port = 5443
$RelayPort = 6443
$script:Procs = @{}
New-Item -ItemType Directory -Force (Join-Path $Run 'results') | Out-Null

function Read-Log([string] $Path) {
    if (-not (Test-Path $Path)) { return @() }
    $fs = [IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
    try { return ([IO.StreamReader]::new($fs).ReadToEnd() -split "`r?`n") | Where-Object { $_ } }
    finally { $fs.Dispose() }
}

function Get-Stamp([string] $Line) { [DateTimeOffset]::Parse($Line.Substring(0, 29)) }

function Wait-Line([string] $Path, [string] $Pattern, [int] $TimeoutSec = 30, [int] $After = 0) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        $lines = @(Read-Log $Path)
        for ($i = $After; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -cmatch $Pattern) {
                return [pscustomobject]@{ Index = $i; Line = $lines[$i]; Time = Get-Stamp $lines[$i]; Match = $Matches }
            }
        }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    return $null
}

function New-StepLogs([string] $Name) {
    $dir = Join-Path $Run "logs/$Name"
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    New-Item -ItemType Directory -Force $dir | Out-Null
    return $dir
}

function Get-LineCount([string] $Path) { @(Read-Log $Path).Count }

function Select-Log([string] $Path, [string] $Pattern, [int] $After = 0) {
    $lines = @(Read-Log $Path)
    for ($i = $After; $i -lt $lines.Count; $i++) { if ($lines[$i] -cmatch $Pattern) { $lines[$i] } }
}

function Start-Probe([string] $Name, [string] $Exe, [string[]] $ArgList) {
    $psi = [Diagnostics.ProcessStartInfo]::new($Exe)
    foreach ($a in $ArgList) { $psi.ArgumentList.Add($a) }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.CreateNoWindow = $true
    $script:Procs[$Name] = [Diagnostics.Process]::Start($psi)
    Write-Host "started $Name pid=$($script:Procs[$Name].Id)"
}

function Send-Line([string] $Name, [string] $Text) {
    $script:Procs[$Name].StandardInput.WriteLine($Text)
    $script:Procs[$Name].StandardInput.Flush()
}

function Stop-Probe([string] $Name) {
    $p = $script:Procs[$Name]
    if ($p -and -not $p.HasExited) { $p.Kill($true); $p.WaitForExit() }
    $script:Procs.Remove($Name)
    Write-Host "stopped $Name"
}

function Stop-All { foreach ($n in @($script:Procs.Keys)) { Stop-Probe $n } }

function Start-Server([string] $Log, [string] $Data = (Join-Path $Run 'server'), [string[]] $Extra = @()) {
    Start-Probe 'server' $ServerExe (@('--port', "$Port", '--data', $Data, '--log', $Log, '--console-log', 'false') + $Extra)
    if (-not (Wait-Line $Log 'CONSOLE ready' 30)) { throw "server did not start; see $Log" }
}

function Get-Pin { (Get-Content (Join-Path $Run 'server/server-pin.txt') -Raw).Trim() }

function Start-Client([string] $Name, [string] $Data, [string] $Log, [string[]] $Extra = @(), [string] $Server = "https://127.0.0.1:$Port") {
    New-Item -ItemType Directory -Force $Data | Out-Null
    Start-Probe $Name $ClientExe (@('--data', $Data, '--log', $Log, '--server', $Server, '--pin', (Get-Pin), '--console-log', 'false') + $Extra)
}

function Reset-Client([string] $Data) {
    & $ClientExe --reset --data $Data | ForEach-Object { Write-Host $_ }
}

function Confirm-Pending([string] $ServerLog, [string] $ClientLog, [string] $Vehicle, [int] $After = 0) {
    $c = Wait-Line $ClientLog 'STATE Pending .*配对码 (\d{6})' 30
    $s = Wait-Line $ServerLog 'PENDING new terminal .*pairingCode=(\d{6})' 30 $After
    if (-not $c -or -not $s) { throw "no pairing code (client=$($c.Line) server=$($s.Line))" }
    $clientCode = ([regex]::Match($c.Line, '配对码 (\d{6})')).Groups[1].Value
    $serverCode = ([regex]::Match($s.Line, 'pairingCode=(\d{6})')).Groups[1].Value
    Send-Line 'server' "confirm $serverCode $Vehicle"
    $accepted = Wait-Line $ClientLog "STATE Connected .*$Vehicle" 15
    return [pscustomobject]@{ ClientCode = $clientCode; ServerCode = $serverCode; ClientLine = $c.Line; ServerLine = $s.Line; Accepted = $accepted }
}

$script:Report = $null
function Start-Result([string] $Title) {
    $script:Report = [Collections.Generic.List[string]]::new()
    $script:Report.Add("# $Title")
    $script:Report.Add('')
    $script:Report.Add("运行时刻：$(Get-Date -Format 'yyyy-MM-ddTHH:mm:sszzz')，本机：$([Environment]::MachineName) $([Environment]::OSVersion.VersionString)")
    $script:Report.Add('')
}
function Add-Result([string] $Text) { $script:Report.Add($Text) }
function Add-Excerpt([string] $Caption, [string[]] $Lines) {
    $script:Report.Add("$Caption")
    $script:Report.Add('')
    $script:Report.Add('```text')
    foreach ($l in $Lines) { $script:Report.Add($l) }
    $script:Report.Add('```')
    $script:Report.Add('')
}
function Save-Result([string] $Name) {
    $path = Join-Path $Run "results/$Name.md"
    Set-Content -Path $path -Value $script:Report -Encoding utf8NoBOM
    Write-Host "wrote $path"
}

function Get-Percentile([double[]] $Values, [double] $P) {
    $sorted = $Values | Sort-Object
    $sorted[[Math]::Min($sorted.Count - 1, [Math]::Ceiling($P * $sorted.Count) - 1)]
}

# ---------------------------------------------------------------- steps

function Step-1 {
    # Step 1 (start and connect) together with 8(a)/(b): a brand-new client.
    $logs = New-StepLogs 'step1'
    $sLog = Join-Path $logs 'server.log'; $cLog = Join-Path $logs 'client-a.log'
    Reset-Client (Join-Path $Run 'client-a')
    Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $Run 'server/terminals.json')
    Start-Server $sLog
    $t0 = Get-Date
    Start-Client 'client-a' (Join-Path $Run 'client-a') $cLog
    $up = Wait-Line $cLog 'STREAM up after' 30
    $pair = Confirm-Pending $sLog $cLog 'agv-a'
    $echo = Wait-Line $sLog 'ECHO s\d+ command=' 30
    Start-Result '第 1 步：启动与连接（并含第 8 步 a、b：全新终端首次连接与确认）'
    Add-Result "- 客户端进程启动到双向流建立：$([int](($up.Time - $t0).TotalMilliseconds)) ms（含首次生成密钥和证书）。"
    Add-Result "- 配对码：客户端屏上 $($pair.ClientCode)，服务端待确认列表 $($pair.ServerCode)，一致：$($pair.ClientCode -eq $pair.ServerCode)。"
    Add-Excerpt '服务端：启动、协商结果、待确认、确认、开始下发命令' (Select-Log $sLog 'START|SERVER-CERT|KESTREL|CALL v1|PENDING|ADMIT|CONSOLE> confirm|CONFIRM|TX s\d+ Command|ECHO' | Select-Object -First 12)
    Add-Excerpt '服务端：确认前收到的上报（state=Pending，不处理内容、不下发命令）' (Select-Log $sLog 'RX s\d+ .*state=Pending' | Select-Object -First 3)
    Add-Excerpt '客户端' (Select-Log $cLog 'IDENTITY|CLIENT|CONNECT|STREAM up|STATE|RX command' | Select-Object -First 9)
    Save-Result 'step1'
    Stop-All
}

function Step-2 {
    # Step 2 soak + step 10 resources. The server sends a command every 10 s.
    $logs = New-StepLogs 'step2'
    $sLog = Join-Path $logs 'server.log'; $cLog = Join-Path $logs 'client-a.log'
    Start-Server $sLog
    Start-Client 'client-a' (Join-Path $Run 'client-a') $cLog
    if (-not (Wait-Line $cLog 'STATE Connected' 30)) { throw 'client-a not accepted; run step 1 first' }
    $start = Get-Date
    Start-Sleep -Seconds ($SoakMinutes * 60)
    Send-Line 'server' 'stats'
    Start-Sleep -Seconds 2
    $end = Get-Date
    $rx = @(Select-Log $sLog 'RX s\d+ seq=')
    $gaps = @($rx | ForEach-Object { if ($_ -match 'gapMs=(\d+)') { [double]$Matches[1] } })
    $seqs = @($rx | ForEach-Object { if ($_ -match 'seq=(\d+)') { [long]$Matches[1] } })
    $missing = 0; for ($i = 1; $i -lt $seqs.Count; $i++) { $missing += [Math]::Max(0, $seqs[$i] - $seqs[$i - 1] - 1) }
    $sent = @(Select-Log $sLog 'TX s\d+ Command').Count
    $echoed = @(Select-Log $sLog 'ECHO s\d+ command=').Count
    $sessionsEnded = @(Select-Log $sLog 'SESSION-END')
    $stale = @(Select-Log $sLog 'STALE key=')
    Start-Result "第 2 步：双向流连续 $SoakMinutes 分钟（并含第 10 步资源记录）"
    Add-Result "- 时段：$($start.ToString('HH:mm:ss')) 至 $($end.ToString('HH:mm:ss'))。"
    Add-Result "- 服务端收到上报 $($rx.Count) 份，序号缺口 $missing 个；上报间隔 p50 $(Get-Percentile $gaps 0.5) ms、p99 $(Get-Percentile $gaps 0.99) ms、最大 $(($gaps | Measure-Object -Maximum).Maximum) ms。"
    Add-Result "- 服务端下发命令 $sent 条，客户端回报 $echoed 条（最后一条可能在统计时还在途中）。"
    Add-Result "- 期间断流（SESSION-END）$($sessionsEnded.Count) 次，上报过期（STALE）$($stale.Count) 次。"
    Add-Excerpt '服务端 stats 与开头几行' (@(Select-Log $sLog 'CALL v1|ADMIT' | Select-Object -First 2) + @(Select-Log $sLog 'STATS'))
    Add-Excerpt '资源（服务端，每分钟一次，取首末各 2 行）' (@(Select-Log $sLog 'RESOURCE' | Select-Object -First 2) + @(Select-Log $sLog 'RESOURCE' | Select-Object -Last 2))
    Add-Excerpt '资源（客户端）' (@(Select-Log $cLog 'RESOURCE' | Select-Object -First 2) + @(Select-Log $cLog 'RESOURCE' | Select-Object -Last 2))
    if ($sessionsEnded) { Add-Excerpt '断流记录' $sessionsEnded }
    Save-Result 'step2'
    Stop-All
}

function Step-3 {
    $logs = New-StepLogs 'step3'
    $sLog = Join-Path $logs 'server.log'; $cLog = Join-Path $logs 'client-a.log'
    Start-Server $sLog
    Start-Client 'client-a' (Join-Path $Run 'client-a') $cLog
    if (-not (Wait-Line $cLog 'STATE Connected' 30)) { throw 'client-a not accepted' }
    Send-Line 'server' 'quiet on'
    $aStart = Get-Date; Start-Sleep -Seconds 300; $aEnd = Get-Date
    Send-Line 'server' 'quiet off'
    Send-Line 'client-a' 'interval 9'
    $bStart = Get-Date; Start-Sleep -Seconds 300; $bEnd = Get-Date
    Send-Line 'server' 'stats'
    Start-Sleep -Seconds 2
    $ended = @(Select-Log $sLog 'SESSION-END|STREAM-END')
    $clientEnded = @(Select-Log $cLog 'STREAM-END')
    $kestrel = @(Select-Log $sLog '(?i)timeout|data rate|keep.?alive|closing|aborted')
    $txA = @(Select-Log $sLog 'TX s\d+ Command' | Where-Object { $t = Get-Stamp $_; $t -gt $aStart.AddSeconds(1) -and $t -lt $aEnd })
    $gapsB = @(Select-Log $sLog 'RX s\d+ seq=' | Where-Object { (Get-Stamp $_) -gt $bStart.AddSeconds(20) } | ForEach-Object { if ($_ -match 'gapMs=(\d+)') { [double]$Matches[1] } })
    Start-Result '第 3 步：低流量长流'
    Add-Result "- (a) 服务端 5 分钟不下发（$($aStart.ToString('HH:mm:ss'))～$($aEnd.ToString('HH:mm:ss'))）：期间服务端下发命令 $($txA.Count) 条。"
    Add-Result "- (b) 客户端每 9 秒上报 5 分钟（$($bStart.ToString('HH:mm:ss'))～$($bEnd.ToString('HH:mm:ss'))）：上报间隔最小 $(($gapsB | Measure-Object -Minimum).Minimum) ms、最大 $(($gapsB | Measure-Object -Maximum).Maximum) ms，共 $($gapsB.Count) 个间隔。"
    Add-Result "- 全程服务端断流 $(@(Select-Log $sLog 'SESSION-END').Count) 次，客户端 STREAM-END $($clientEnded.Count) 次。"
    Add-Excerpt 'Kestrel 生效的限值（启动时记录）' (Select-Log $sLog 'KESTREL')
    Add-Excerpt '服务端：与超时、保活、数据速率有关的 Kestrel 日志（无则为空）' ($kestrel | Select-Object -First 20)
    Add-Excerpt '服务端：stats 与模式切换' (Select-Log $sLog 'CONSOLE> |QUIET|STATS')
    if ($ended) { Add-Excerpt '断流记录' $ended }
    Save-Result 'step3'
    Stop-All
}

function Invoke-Cut([string] $sLog, [string] $cLog, [int] $HoldSec, [int] $Round) {
    $sAfter = Get-LineCount $sLog; $cAfter = Get-LineCount $cLog
    Send-Line 'linkcut' 'cut'
    $cut = Get-Date
    Start-Sleep -Seconds $HoldSec
    Send-Line 'linkcut' 'restore'
    $restore = Get-Date
    $back = Wait-Line $sLog 'FRESH-AGAIN' 90 $sAfter
    $admit = @(Select-Log $sLog 'ADMIT s\d+ .*state=Confirmed' $sAfter) | Select-Object -Last 1
    $stale = Wait-Line $sLog 'STALE key=' 1 $sAfter
    $sEnd = Wait-Line $sLog 'SESSION-END' 1 $sAfter
    $cEnd = Wait-Line $cLog 'STREAM-END' 1 $cAfter
    $connects = @(Select-Log $cLog 'CONNECT starting' $cAfter | Where-Object { (Get-Stamp $_) -lt $restore })
    $up = @(Select-Log $cLog 'STREAM up' $cAfter) | Where-Object { (Get-Stamp $_) -ge $restore } | Select-Object -First 1
    [pscustomobject]@{
        Round = $Round
        Cut = $cut.ToString('HH:mm:ss.fff')
        StaleAfterCutS = if ($stale) { [Math]::Round(($stale.Time - $cut).TotalSeconds, 2) } else { $null }
        ClientStreamEndAfterCutS = if ($cEnd) { [Math]::Round(($cEnd.Time - $cut).TotalSeconds, 1) } else { $null }
        ServerSessionEndAfterCutS = if ($sEnd) { [Math]::Round(($sEnd.Time - $cut).TotalSeconds, 1) } else { $null }
        ConnectAttemptsDuringCut = $connects.Count
        ClientUpAfterRestoreS = if ($up) { [Math]::Round(((Get-Stamp $up) - $restore).TotalSeconds, 1) } else { $null }
        FreshAgainAfterRestoreS = if ($back) { [Math]::Round(($back.Time - $restore).TotalSeconds, 1) } else { $null }
        SameTerminal = [bool]($admit -match 'state=Confirmed vehicle=agv-a')
        ConnectTimes = ($connects | ForEach-Object { (Get-Stamp $_).ToString('HH:mm:ss') }) -join ' '
    }
}

function Step-LinkCut([string] $StepNo, [int] $Rounds, [int] $HoldSec, [string[]] $ServerArgs = @()) {
    $logs = New-StepLogs "step$StepNo"
    $sLog = Join-Path $logs 'server.log'; $cLog = Join-Path $logs 'client-a.log'
    Start-Server $sLog -Extra $ServerArgs
    Start-Probe 'linkcut' $LinkCutExe @('--listen', "127.0.0.1:$RelayPort", '--target', "127.0.0.1:$Port", '--log', (Join-Path $logs 'linkcut.log'))
    Start-Sleep -Seconds 1
    Start-Client 'client-a' (Join-Path $Run 'client-a') $cLog -Server "https://127.0.0.1:$RelayPort"
    if (-not (Wait-Line $cLog 'STATE Connected' 30)) { throw 'client-a not accepted' }
    Start-Sleep -Seconds 5
    $rows = foreach ($r in 1..$Rounds) {
        $row = Invoke-Cut $sLog $cLog $HoldSec $r
        Write-Host ($row | ConvertTo-Json -Compress)
        $row
        Start-Sleep -Seconds 5
    }
    return [pscustomobject]@{ Rows = $rows; ServerLog = $sLog; ClientLog = $cLog; LinkLog = (Join-Path $logs 'linkcut.log') }
}

function Step-4 {
    $r = Step-LinkCut 4 10 60
    Start-Result '第 4 步：静默断链（用本机 TCP 中继 linkcut 模拟丢包不回 RST，阻断 60 秒，10 次）'
    Add-Result '本机没有管理员权限，加不了防火墙规则；回环流量也不经过 Windows 防火墙。linkcut 在阻断期间不转发任何字节、不关闭任何连接，新连接挂在中继上不往前接，效果与"丢弃不回复"的出站阻断相同；但 TCP 重传、SYN 重试这些内核层行为不会出现（中继本身照常 ACK），所以"连接层多久关闭"只代表 HTTP/2 保活的判定时间。'
    Add-Result ''
    Add-Result '| 次 | 阻断时刻 | 阻断→服务端判上报过期 (s) | 阻断→客户端断流 (s) | 阻断→服务端断流 (s) | 阻断期间重连尝试 | 恢复→客户端重新建流 (s) | 恢复→服务端收到新鲜上报 (s) | 认作同一台已登记终端 |'
    Add-Result '| --- | --- | --- | --- | --- | --- | --- | --- | --- |'
    foreach ($x in $r.Rows) { Add-Result "| $($x.Round) | $($x.Cut) | $($x.StaleAfterCutS) | $($x.ClientStreamEndAfterCutS) | $($x.ServerSessionEndAfterCutS) | $($x.ConnectAttemptsDuringCut) | $($x.ClientUpAfterRestoreS) | $($x.FreshAgainAfterRestoreS) | $($x.SameTerminal) |" }
    Add-Result ''
    Add-Excerpt '第 1 次的服务端日志（阻断前后）' (Select-Log $r.ServerLog 'STALE|SESSION-END|SUPERSEDE|ADMIT|FRESH-AGAIN|Connection id' | Select-Object -First 14)
    Add-Excerpt '第 1 次的客户端日志' (Select-Log $r.ClientLog 'STREAM-END|STATE|RETRY|CONNECT|STREAM up' | Select-Object -First 14)
    Save-Result 'step4'
    Stop-All
}

function Step-4k {
    # Same as step 4 but the server also pings (Kestrel HTTP/2 keepalive), to
    # see whether the server reclaims a dead connection on its own.
    $r = Step-LinkCut '4k' 3 60 @('--ping-delay', '15', '--ping-timeout', '15')
    Start-Result '第 4 步对照：服务端也开 HTTP/2 保活（ping 间隔 15 秒、超时 15 秒），阻断 60 秒，3 次'
    Add-Result '| 次 | 阻断时刻 | 阻断→判过期 (s) | 阻断→客户端断流 (s) | 阻断→服务端断流 (s) | 恢复→客户端重新建流 (s) | 恢复→新鲜上报 (s) | 同一台 |'
    Add-Result '| --- | --- | --- | --- | --- | --- | --- | --- |'
    foreach ($x in $r.Rows) { Add-Result "| $($x.Round) | $($x.Cut) | $($x.StaleAfterCutS) | $($x.ClientStreamEndAfterCutS) | $($x.ServerSessionEndAfterCutS) | $($x.ClientUpAfterRestoreS) | $($x.FreshAgainAfterRestoreS) | $($x.SameTerminal) |" }
    Add-Result ''
    Add-Excerpt '服务端（第 1 次）' (Select-Log $r.ServerLog 'KESTREL|STALE|SESSION-END|(?i:keep ?alive|timed out|ping)' | Select-Object -First 10)
    Save-Result 'step4k'
    Stop-All
}

function Step-5 {
    $r = Step-LinkCut 5 3 120
    Start-Result '第 5 步：真断 Wi-Fi 的本机替代（linkcut 阻断 120 秒，3 次）'
    Add-Result '真断无线网卡在本机做不了，这里和第 4 步一样用 linkcut 静默阻断代替，阻断 2 分钟。所以本步只验证判据里与软件有关的部分（10 秒判过期、退避上限、恢复后 30 秒内重新建流），网卡掉线、重新关联 AP、DHCP 这些真实因素要留到第二阶段。'
    Add-Result ''
    Add-Result '| 次 | 阻断时刻 | 阻断→判过期 (s) | 阻断→客户端断流 (s) | 阻断期间重连尝试 | 尝试时刻 | 恢复→客户端重新建流 (s) | 恢复→新鲜上报 (s) | 同一台 |'
    Add-Result '| --- | --- | --- | --- | --- | --- | --- | --- | --- |'
    foreach ($x in $r.Rows) { Add-Result "| $($x.Round) | $($x.Cut) | $($x.StaleAfterCutS) | $($x.ClientStreamEndAfterCutS) | $($x.ConnectAttemptsDuringCut) | $($x.ConnectTimes) | $($x.ClientUpAfterRestoreS) | $($x.FreshAgainAfterRestoreS) | $($x.SameTerminal) |" }
    Add-Result ''
    Add-Excerpt '客户端退避记录（第 1 次）' (Select-Log $r.ClientLog 'STREAM-END|RETRY|CONNECT|STREAM up' | Select-Object -First 24)
    Save-Result 'step5'
    Stop-All
}

function Step-6 {
    $logs = New-StepLogs 'step6'
    $cLog = Join-Path $logs 'client-a.log'
    Start-Server (Join-Path $logs 'server-1.log')
    Start-Client 'client-a' (Join-Path $Run 'client-a') $cLog
    if (-not (Wait-Line $cLog 'STATE Connected' 30)) { throw 'client-a not accepted' }
    Start-Sleep -Seconds 5
    $rows = foreach ($down in 10, 60) {
        $cAfter = Get-LineCount $cLog
        Stop-Probe 'server'
        $killed = Get-Date
        Start-Sleep -Seconds $down
        $sLog = Join-Path $logs "server-down$down.log"
        Start-Server $sLog
        $started = Get-Date
        $up = Wait-Line $cLog 'STATE Connected' 60 $cAfter
        $disc = Wait-Line $cLog 'STATE Disconnected' 1 $cAfter
        [pscustomobject]@{
            DownSec = $down
            DisconnectedShownAfterKillS = if ($disc) { [Math]::Round(($disc.Time - $killed).TotalSeconds, 2) } else { $null }
            ReconnectedAfterStartS = if ($up) { [Math]::Round(($up.Time - $started).TotalSeconds, 1) } else { $null }
            Attempts = @(Select-Log $cLog 'CONNECT starting' $cAfter).Count
            Admit = (Select-Log $sLog 'ADMIT' | Select-Object -First 1)
        }
        Start-Sleep -Seconds 5
    }
    Start-Result '第 6 步：服务端重启（直接结束进程，停 10 秒与 60 秒各一次）'
    Add-Result '| 停机时长 (s) | 结束进程→客户端显示"与服务端断开" (s) | 服务端启动完成→客户端重新连上 (s) | 期间连接尝试次数 |'
    Add-Result '| --- | --- | --- | --- |'
    foreach ($x in $rows) { Add-Result "| $($x.DownSec) | $($x.DisconnectedShownAfterKillS) | $($x.ReconnectedAfterStartS) | $($x.Attempts) |" }
    Add-Result ''
    Add-Excerpt '重启后服务端认出的终端' ($rows | ForEach-Object { $_.Admit })
    Add-Excerpt '客户端' (Select-Log $cLog 'STREAM-END|STATE|RETRY' | Select-Object -First 30)
    Save-Result 'step6'
    Stop-All
}

function Step-7 {
    $logs = New-StepLogs 'step7'
    $sLog = Join-Path $logs 'server.log'
    Start-Server $sLog
    # (a) a client that speaks plaintext HTTP/2 to the TLS endpoint.
    $aLog = Join-Path $logs 'client-plain.log'
    Start-Client 'client-plain' (Join-Path $Run 'client-a') $aLog -Server "http://127.0.0.1:$Port"
    Start-Sleep -Seconds 15
    Stop-Probe 'client-plain'
    Stop-Probe 'server'
    # (b) the server presents a certificate the client has not pinned.
    $bLog = Join-Path $logs 'server-other-cert.log'
    $otherData = Join-Path $Run 'server-other'
    Start-Server $bLog -Data $otherData
    $cLog = Join-Path $logs 'client-a-pinned.log'
    Start-Client 'client-a' (Join-Path $Run 'client-a') $cLog
    Start-Sleep -Seconds 15
    Stop-All
    Start-Result '第 7 步：TLS 与服务端身份'
    Add-Result "- (a) 明文客户端连加密端口：客户端建立流 $(@(Select-Log $aLog 'STREAM up').Count) 次，服务端收到调用 $(@(Select-Log $sLog 'CALL v1').Count) 次。"
    Add-Excerpt '(a) 客户端' (Select-Log $aLog 'STREAM-END|STATE' | Select-Object -First 4)
    Add-Excerpt '(a) 服务端（Kestrel 调试日志）' (Select-Log $sLog '(?i)tls|ssl|handshake|authenticate|Connection id' | Select-Object -First 8)
    Add-Result "- (b) 服务端换一张证书（指纹 $((Get-Content (Join-Path $otherData 'server-pin.txt') -Raw).Trim().Substring(0,12))…，客户端钉的是 $((Get-Pin).Substring(0,12))…）：客户端发出上报 $(@(Select-Log $cLog 'TX seq=').Count) 份，服务端收到调用 $(@(Select-Log $bLog 'CALL v1').Count) 次。"
    Add-Excerpt '(b) 客户端' (Select-Log $cLog 'PIN-MISMATCH|STREAM-END|STATE' | Select-Object -First 6)
    Add-Excerpt '(b) 服务端' (Select-Log $bLog '(?i)CALL|tls|authenticate|Connection id' | Select-Object -First 6)
    Save-Result 'step7'
}

function Step-8 {
    $logs = New-StepLogs 'step8'
    $sLog = Join-Path $logs 'server.log'
    Start-Server $sLog
    Start-Probe 'linkcut' $LinkCutExe @('--listen', "127.0.0.1:$RelayPort", '--target', "127.0.0.1:$Port", '--log', (Join-Path $logs 'linkcut.log'))
    Start-Result '第 8 步：每车凭据'

    # (a)(b) a second brand-new terminal.
    Reset-Client (Join-Path $Run 'client-b')
    $bLog = Join-Path $logs 'client-b.log'
    $after = Get-LineCount $sLog
    Start-Client 'client-b' (Join-Path $Run 'client-b') $bLog
    Start-Sleep -Seconds 12
    $pendingRx = @(Select-Log $sLog 'RX s\d+ .*state=Pending' $after)
    $pendingTx = @(Select-Log $sLog 'TX s\d+ Command' $after)
    $pair = Confirm-Pending $sLog $bLog 'agv-b' $after
    $echo = Wait-Line $sLog 'ECHO s\d+ command=' 30 $after
    Add-Result "- (a) 全新终端：配对码 客户端 $($pair.ClientCode) / 服务端 $($pair.ServerCode)，一致 $($pair.ClientCode -eq $pair.ServerCode)；确认前 12 秒内收到上报 $($pendingRx.Count) 份（state=Pending，不处理），下发命令 $($pendingTx.Count) 条。"
    Add-Result "- (b) 确认后：客户端进入已连接 $([bool]$pair.Accepted)；首个命令回显 $($echo.Line)"
    Add-Excerpt '(a)(b) 服务端' (Select-Log $sLog 'PENDING|ADMIT|CONFIRM|ECHO' $after | Select-Object -First 6)
    Add-Excerpt '(a)(b) 客户端' (Select-Log $bLog 'IDENTITY|STATE' | Select-Object -First 4)

    # (c) an unregistered key that claims a confirmed vehicle.
    $cData = Join-Path $Run 'client-c'
    Reset-Client $cData
    $cLog = Join-Path $logs 'client-c.log'
    $after = Get-LineCount $sLog
    Start-Client 'client-c' $cData $cLog @('--claim-vehicle', 'agv-a')
    Start-Sleep -Seconds 8
    Stop-Probe 'client-c'
    Add-Result "- (c) 未登记的密钥自称 agv-a：服务端拒绝 $(@(Select-Log $sLog 'REFUSE .*Unauthenticated' $after).Count) 次，客户端进入已连接 $(@(Select-Log $cLog 'STATE Connected').Count) 次。"
    Add-Excerpt '(c)' (@(Select-Log $sLog 'REFUSE' $after | Select-Object -First 2) + @(Select-Log $cLog 'STREAM-END|STATE' | Select-Object -First 2))
    Reset-Client $cData

    # (d)1 the same key reconnects quickly while the server still holds the old connection.
    $aLog = Join-Path $logs 'client-a.log'
    Start-Client 'client-a' (Join-Path $Run 'client-a') $aLog -Server "https://127.0.0.1:$RelayPort"
    if (-not (Wait-Line $aLog 'STATE Connected' 30)) { throw 'client-a not accepted' }
    Start-Sleep -Seconds 3
    $after = Get-LineCount $sLog
    $rows = foreach ($i in 1..10) {
        $sA = Get-LineCount $sLog
        Send-Line 'linkcut' 'orphan'
        $t = Get-Date
        $sup = Wait-Line $sLog 'SUPERSEDE key=' 20 $sA
        $fresh = Wait-Line $sLog 'ADMIT s\d+ .*state=Confirmed vehicle=agv-a' 20 $sA
        [pscustomobject]@{ Round = $i; SupersedeAfterS = if ($sup) { [Math]::Round(($sup.Time - $t).TotalSeconds, 2) } else { $null }; Admitted = [bool]$fresh }
        Start-Sleep -Seconds 6
    }
    $alerts = @(Select-Log $sLog 'ALERT' $after)
    $refused = @(Select-Log $sLog 'REFUSE' $after)
    Add-Result "- (d)① 断链后用同一把密钥很快重连（服务端旧连接仍开着）10 次：顶替 $(@($rows | Where-Object { $null -ne $_.SupersedeAfterS }).Count) 次、重新接纳 $(@($rows | Where-Object Admitted).Count) 次，告警 $($alerts.Count) 条，拒绝 $($refused.Count) 次；断链到顶替耗时（秒）：$(($rows | ForEach-Object SupersedeAfterS) -join ' ')。"
    Add-Excerpt '(d)① 服务端（第 1 次）' (Select-Log $sLog 'SUPERSEDE|ADMIT|SESSION-END|ALERT' $after | Select-Object -First 4)

    # (d)2 two processes with the same key at once (a cloned disk, both running).
    $after = Get-LineCount $sLog
    $a2Log = Join-Path $logs 'client-a-clone.log'
    Start-Client 'client-a2' (Join-Path $Run 'client-a') $a2Log -Server "https://127.0.0.1:$RelayPort"
    $alert = Wait-Line $sLog 'ALERT clone-suspected' 30 $after
    Start-Sleep -Seconds 20
    $refusedBoth = @(Select-Log $sLog 'ALERT clone-suspected key=.* tried to connect again' $after)
    $instances = @($refusedBoth | ForEach-Object { if ($_ -match 'instance=(\w+)') { $Matches[1] } } | Sort-Object -Unique)
    Add-Result "- (d)② 两个进程同时用同一把密钥：告警 $([bool]$alert)；告警后两个实例都被拒（不同实例数 $($instances.Count)），两边进入被拒状态：原实例 $(@(Select-Log $aLog 'STATE Refused').Count -gt 0)、克隆实例 $(@(Select-Log $a2Log 'STATE Refused').Count -gt 0)。"
    Add-Excerpt '(d)② 服务端' (@($alert.Line) + ($refusedBoth | Select-Object -First 4))
    Add-Excerpt '(d)② 两个客户端' (@(Select-Log $aLog 'STATE Refused' | Select-Object -First 1) + @(Select-Log $a2Log 'STATE Refused' | Select-Object -First 1))

    # Recovery: the engineer clears the clone and re-confirms the original.
    Stop-Probe 'client-a2'
    $after = Get-LineCount $sLog
    Send-Line 'server' 'forget agv-a'
    $re = Wait-Line $sLog 'PENDING new terminal .*pairingCode=(\d{6})' 150 $after
    if ($re) {
        $code = ([regex]::Match($re.Line, 'pairingCode=(\d{6})')).Groups[1].Value
        Send-Line 'server' "confirm $code agv-a"
        $back = Wait-Line $sLog 'ADMIT s\d+ .*state=Confirmed vehicle=agv-a|CONFIRM key=.* agv-a' 60 $after
    }
    Add-Result "- 收尾：停掉克隆实例、forget 后原终端重新待确认并确认为 agv-a：$([bool]$back)。"
    Add-Excerpt '收尾' (Select-Log $sLog 'FORGET|PENDING|CONFIRM' $after | Select-Object -First 4)

    # (e) revoke agv-b.
    $after = Get-LineCount $sLog; $bAfter = Get-LineCount $bLog
    Send-Line 'server' 'revoke agv-b'
    Start-Sleep -Seconds 20
    Add-Result "- (e) 吊销 agv-b：当前流以 PermissionDenied 结束 $(@(Select-Log $bLog 'STREAM-END .*PermissionDenied' $bAfter).Count -gt 0)，之后重连被拒 $(@(Select-Log $sLog 'REFUSE .*PermissionDenied' $after).Count) 次，客户端尝试 $(@(Select-Log $bLog 'CONNECT starting' $bAfter).Count) 次。"
    Add-Excerpt '(e)' (@(Select-Log $sLog 'REVOKE|REFUSE' $after | Select-Object -First 3) + @(Select-Log $bLog 'STREAM-END|STATE|RETRY' $bAfter | Select-Object -First 5))
    Stop-Probe 'client-b'
    Reset-Client (Join-Path $Run 'client-b')
    Save-Result 'step8'
    Stop-All
}

function Step-8f {
    $logs = New-StepLogs 'step8f'
    $sLog = Join-Path $logs 'server.log'
    $tData = Join-Path $Run 'client-tpm'
    Reset-Client $tData
    Start-Server $sLog
    $tLog = Join-Path $logs 'client-tpm.log'
    $after = Get-LineCount $sLog
    Start-Client 'client-tpm' $tData $tLog @('--key-provider', 'tpm')
    $pair = $null
    try { $pair = Confirm-Pending $sLog $tLog 'agv-t' $after } catch { Write-Host $_ }
    $echo = Wait-Line $sLog 'ECHO s\d+ command=' 30 $after
    Stop-All
    Start-Result '第 8 步 (f)：私钥放进 TPM（Microsoft Platform Crypto Provider）'
    Add-Result "- 本机 TPM（tpmtool getdeviceinformation，只读）：见下方摘录。"
    Add-Excerpt 'tpmtool' (tpmtool getdeviceinformation 2>&1 | Select-Object -First 12)
    Add-Result "- 客户端在 TPM 里生成密钥并握手：配对码一致 $($pair.ClientCode -eq $pair.ServerCode)，确认后进入已连接 $([bool]$pair.Accepted)，命令回显 $([bool]$echo)。"
    Add-Excerpt '客户端' (Select-Log $tLog 'IDENTITY|STATE|FATAL|STREAM-END' | Select-Object -First 6)
    Add-Excerpt '服务端' (Select-Log $sLog 'CALL v1|PENDING|CONFIRM|ADMIT|ECHO' | Select-Object -First 6)
    Reset-Client $tData
    Save-Result 'step8f'
}

function Step-9 {
    $logs = New-StepLogs 'step9'
    $sLog = Join-Path $logs 'server.log'
    Start-Server $sLog
    Start-Result '第 9 步：版本'
    $aLog = Join-Path $logs 'client-a.log'
    Start-Client 'client-a' (Join-Path $Run 'client-a') $aLog
    $ok = Wait-Line $aLog 'STATE Connected' 30
    Add-Result "- (a) 客户端主版本 1、服务端支持 1 和 2：接受 $([bool]$ok)。"
    Add-Excerpt '(a)' (@(Select-Log $sLog 'CALL v1|ADMIT' | Select-Object -First 2) + @($ok.Line))
    foreach ($major in 3, 0) {
        $vData = Join-Path $Run "client-v$major"
        Reset-Client $vData
        $vLog = Join-Path $logs "client-major$major.log"
        $after = Get-LineCount $sLog
        Start-Client "client-v$major" $vData $vLog @('--major', "$major")
        Start-Sleep -Seconds 120
        Stop-Probe "client-v$major"
        $attempts = @(Select-Log $vLog 'CONNECT starting')
        $times = @($attempts | ForEach-Object { Get-Stamp $_ })
        $maxPerMinute = 0
        foreach ($t in $times) { $n = @($times | Where-Object { $_ -ge $t -and $_ -lt $t.AddSeconds(60) }).Count; $maxPerMinute = [Math]::Max($maxPerMinute, $n) }
        Add-Result "- (b) 客户端主版本 ${major}：服务端 FailedPrecondition 拒绝 $(@(Select-Log $sLog 'REFUSE-VERSION' $after).Count) 次；2 分钟内尝试 $($attempts.Count) 次，任意 60 秒内最多 $maxPerMinute 次；客户端状态：$(@(Select-Log $vLog 'STATE' | Select-Object -First 1))"
        Add-Excerpt "(b) 主版本 $major" (@(Select-Log $sLog 'REFUSE-VERSION' $after | Select-Object -First 1) + @(Select-Log $vLog 'STREAM-END|STATE|RETRY' | Select-Object -First 8))
        Reset-Client $vData
    }
    $after = Get-LineCount $sLog; $aAfter = Get-LineCount $aLog
    Send-Line 'client-a' 'extra on'
    Send-Line 'server' 'hint on'
    Start-Sleep -Seconds 30
    Add-Result "- (c) 双方各多带一个对方不认识的字段 30 秒：服务端收到带未知字段 50 的上报 $(@(Select-Log $sLog 'RX s\d+ .*unknownFields=\[50\]' $after).Count) 份，客户端收到带未知字段 50 的命令 $(@(Select-Log $aLog 'RX command=.*unknownFields=\[50\]' $aAfter).Count) 条，期间断流 $(@(Select-Log $sLog 'SESSION-END' $after).Count + @(Select-Log $aLog 'STREAM-END' $aAfter).Count) 次。"
    Add-Excerpt '(c)' (@(Select-Log $sLog 'RX s\d+ .*unknownFields=\[50\]|ECHO' $after | Select-Object -First 3) + @(Select-Log $aLog 'RX command=.*unknownFields' $aAfter | Select-Object -First 2))
    Save-Result 'step9'
    Stop-All
}

function Step-Cleanup {
    Start-Result '第 11 步（本机部分）：清理核对'
    foreach ($d in 'client-a', 'client-b', 'client-c', 'client-tpm', 'client-v3', 'client-v0', 'client-backoff') { Reset-Client (Join-Path $Run $d) }
    $certs = @(Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -EQ 'CN=simple-probe-terminal')
    $procs = @(Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($Bin, [StringComparison]::OrdinalIgnoreCase) })
    Add-Result "- CurrentUser\My 里 CN=simple-probe-terminal 的证书：$($certs.Count) 张。"
    Add-Result "- 由本次发布目录启动、仍在运行的进程：$($procs.Count) 个。"
    Add-Result '- 本机没有添加过防火墙规则（没有管理员权限，也不需要）。'
    $rules = @(Get-NetFirewallRule -DisplayName '*simple-probe*' -ErrorAction SilentlyContinue)
    Add-Result "- 名称含 simple-probe 的防火墙规则：$($rules.Count) 条。"
    Save-Result 'cleanup'
}

try {
    foreach ($s in $Steps) { & "Step-$s" }
}
finally {
    Stop-All
}
