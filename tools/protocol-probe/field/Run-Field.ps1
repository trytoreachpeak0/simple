#Requires -Version 7
<#
.SYNOPSIS
Runs the #18 test steps against a real server machine and one real vehicle PC
over the remote-ops SSH channels (stage two). Every remote process is one of
the probe's own executables under -ServerRoot / -ClientRoot.

.DESCRIPTION
The probe executables must already be unpacked on both machines
(<ServerRoot>/server, <ClientRoot>/client, <ClientRoot>/linkcut). Each remote
process is started through `ssh <alias>`, driven through its standard input,
and its console output is mirrored into <Run>/mirror/ on this machine, which is
what the checks read. The remote log files are copied back by step 'Collect'.

Over SSH a public-key logon has no DPAPI master key, so the server imports its
PFX into the machine key store (--key-load machine) and the client keeps its
key and certificate in the machine store (--key-store machine). Both need the
SSH account to be a local administrator.

Nothing here touches anything but the probe: no firewall rule, no service, no
other process. Step 4 uses linkcut on the vehicle PC (listening on 127.0.0.1
only) because the vehicle PC's firewall profile is off.

Every ssh and scp call is checked: a non-zero exit stops the run. An alias of
'.' runs that side on this machine without ssh, for checking the script itself
on a development machine (add -KeyStore user when this machine's account is not
an administrator).

.EXAMPLE
pwsh -File tools/protocol-probe/field/Run-Field.ps1 -Run F:/simple/probe-run/18-field -ServerAlias factory01 -ClientAlias agv03 -ServerBind 172.19.205.222 -Steps 1,3,4,6,7,8,9,2
pwsh -File tools/protocol-probe/field/Run-Field.ps1 -Run F:/simple/probe-run/18-field -ServerAlias factory01 -ClientAlias agv03 -ServerBind 172.19.205.222 -Steps Collect,Cleanup
#>
param(
    [Parameter(Mandatory)] [string] $Run,
    [Parameter(Mandatory)] [string[]] $Steps,
    [Parameter(Mandatory)] [string] $ServerAlias,
    [Parameter(Mandatory)] [string] $ClientAlias,
    [Parameter(Mandatory)] [string] $ServerBind,
    [string] $ServerRoot = 'D:/simple-probe',
    [string] $ClientRoot = 'C:/simple-probe',
    [ValidateSet('machine', 'user')] [string] $KeyStore = 'machine',
    [int] $SoakMinutes = 30
)

$ErrorActionPreference = 'Stop'
$Steps = $Steps -split ','
$Run = [IO.Path]::GetFullPath($Run)
$Port = 5443
$RelayPort = 6443
# Ordered, so that Stop-All can stop terminals before the server.
$script:Procs = [ordered]@{}
$script:Pin = $null
foreach ($d in 'mirror', 'results', 'logs') { New-Item -ItemType Directory -Force (Join-Path $Run $d) | Out-Null }

# ---------------------------------------------------------------- helpers

function Read-Log([string] $Path) {
    if (-not (Test-Path $Path)) { return @() }
    $fs = [IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
    try { return ([IO.StreamReader]::new($fs).ReadToEnd() -split "`r?`n") | Where-Object { $_ -match '^\d{4}-\d\d-\d\dT' } }
    finally { $fs.Dispose() }
}

function Get-Stamp([string] $Line) { [DateTimeOffset]::Parse($Line.Substring(0, 29)) }

function Wait-Line([string] $Path, [string] $Pattern, [int] $TimeoutSec = 30, [int] $After = 0) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        $lines = @(Read-Log $Path)
        for ($i = $After; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -cmatch $Pattern) {
                return [pscustomobject]@{ Index = $i; Line = $lines[$i]; Time = Get-Stamp $lines[$i] }
            }
        }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $deadline)
    return $null
}

function Get-LineCount([string] $Path) { @(Read-Log $Path).Count }

function Select-Log([string] $Path, [string] $Pattern, [int] $After = 0) {
    $lines = @(Read-Log $Path)
    for ($i = $After; $i -lt $lines.Count; $i++) { if ($lines[$i] -cmatch $Pattern) { $lines[$i] } }
}

# Runs a script on the remote machine. It travels as -EncodedCommand: piping it
# to 'pwsh -Command -' silently skips a multi-line block at the end of the input.
# A non-zero exit (ssh cannot connect, or the script failed) throws.
function Invoke-Remote([string] $Alias, [string] $Script) {
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Script))
    if ($Alias -eq '.') { pwsh -NoProfile -EncodedCommand $encoded }
    else { ssh -T $Alias "pwsh -NoProfile -EncodedCommand $encoded" }
    if ($LASTEXITCODE -ne 0) { throw "remote command on '$Alias' failed with exit code $LASTEXITCODE" }
}

function Start-Remote([string] $Name, [string] $Alias, [string] $Exe, [string[]] $ArgList) {
    $mirror = Join-Path $Run "mirror/$Name.log"
    if ($Alias -eq '.') {
        $psi = [Diagnostics.ProcessStartInfo]::new($Exe)
        foreach ($a in $ArgList) { $psi.ArgumentList.Add($a) }
    }
    else {
        $command = "& '$Exe' " + ($ArgList -join ' ')
        $psi = [Diagnostics.ProcessStartInfo]::new('ssh')
        foreach ($a in '-T', $Alias, $command) { $psi.ArgumentList.Add($a) }
    }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $p = [Diagnostics.Process]::Start($psi)
    $out = [IO.FileStream]::new($mirror, 'Append', 'Write', 'ReadWrite', 1)
    $err = [IO.FileStream]::new("$mirror.err", 'Append', 'Write', 'ReadWrite', 1)
    $script:Procs[$Name] = [pscustomobject]@{
        Process = $p; Alias = $Alias; Mirror = $mirror; Out = $out; Err = $err
        OutTask = $p.StandardOutput.BaseStream.CopyToAsync($out)
        ErrTask = $p.StandardError.BaseStream.CopyToAsync($err)
    }
    Write-Host "started $Name on $Alias"
}

function Send-Line([string] $Name, [string] $Text) {
    $p = $script:Procs[$Name].Process
    $p.StandardInput.WriteLine($Text)
    $p.StandardInput.Flush()
}

# Stops a remote probe process: 'quit' on its console first (the server then
# ends its streams and disposes its certificate, which removes the temporary key
# container), and only if it has not exited in time (40 s for a server, whose
# Kestrel shutdown timeout is 30 s; 10 s otherwise), or with -Kill, Stop-Process
# by its pid.
function Stop-Remote([string] $Name, [switch] $Kill) {
    $entry = $script:Procs[$Name]
    if (-not $entry) { return }
    if (-not $Kill -and -not $entry.Process.HasExited) {
        try { Send-Line $Name 'quit' } catch { Write-Host "quit to $Name failed: $_" }
    }
    $grace = $Name -like 'server*' ? 40000 : 10000
    $quitAt = Get-Date
    if ($Kill -or -not $entry.Process.WaitForExit($grace)) {
        $start = @(Select-Log $entry.Mirror 'START (server|client) pid=\d+') | Select-Object -Last 1
        if ($start -match 'pid=(\d+)') {
            Invoke-Remote $entry.Alias "`$p = @(Get-Process | Where-Object { `$_.Id -eq $($Matches[1]) -and `$_.Path -like '*simple-probe*' }); if (`$p) { `$p | Stop-Process -Force }" | Out-Null
        }
        Write-Host "killed $Name"
    }
    if (-not $entry.Process.WaitForExit(10000)) { $entry.Process.Kill($true) }
    $entry.OutTask.Wait(3000) | Out-Null
    $entry.ErrTask.Wait(3000) | Out-Null
    $entry.Out.Dispose(); $entry.Err.Dispose()
    $script:Procs.Remove($Name)
    Write-Host "stopped $Name after $([int]((Get-Date) - $quitAt).TotalMilliseconds) ms"
}

# Terminals and linkcut first, servers last: a server that is quit while
# terminals are still connected has to end their streams first.
function Stop-All {
    $names = @($script:Procs.Keys)
    foreach ($n in @($names | Where-Object { $_ -notlike 'server*' }) + @($names | Where-Object { $_ -like 'server*' })) { Stop-Remote $n }
}

function Start-Server([string] $Name, [string] $Log, [string] $Data = "$ServerRoot/data", [string[]] $Extra = @()) {
    $mirror = Join-Path $Run "mirror/$Name.log"
    $after = Get-LineCount $mirror
    Start-Remote $Name $ServerAlias "$ServerRoot/server/simple-probe-server.exe" (@('--bind', $ServerBind, '--port', "$Port", '--data', $Data, '--key-load', $KeyStore, '--log', "$ServerRoot/logs/$Log") + $Extra)
    if (-not (Wait-Line $mirror 'CONSOLE ready' 60 $after)) { throw "server $Name did not start; see $mirror" }
}

function Get-Pin { (Invoke-Remote $ServerAlias "Get-Content '$ServerRoot/data/server-pin.txt'").Trim() }

function Start-Client([string] $Name, [string] $Data, [string] $Log, [string[]] $Extra = @(), [string] $Server = "https://${ServerBind}:$Port") {
    if (-not $script:Pin) { $script:Pin = Get-Pin }
    Start-Remote $Name $ClientAlias "$ClientRoot/client/simple-probe-client.exe" (@('--data', "$ClientRoot/$Data", '--key-store', $KeyStore, '--log', "$ClientRoot/logs/$Log", '--server', $Server, '--pin', $script:Pin) + $Extra)
}

function Reset-Client([string] $Data) {
    Invoke-Remote $ClientAlias "& '$ClientRoot/client/simple-probe-client.exe' --reset --data '$ClientRoot/$Data'" | ForEach-Object { Write-Host $_ }
}

function Start-LinkCut([string] $Name, [string] $Log) {
    Start-Remote $Name $ClientAlias "$ClientRoot/linkcut/simple-probe-linkcut.exe" @('--listen', "127.0.0.1:$RelayPort", '--target', "${ServerBind}:$Port", '--allow-remote-target', '--log', "$ClientRoot/logs/$Log")
    if (-not (Wait-Line (Join-Path $Run "mirror/$Name.log") 'LINKCUT listening' 30)) { throw 'linkcut did not start' }
}

function Confirm-Pending([string] $ServerMirror, [string] $ClientMirror, [string] $Vehicle, [int] $SAfter = 0, [int] $CAfter = 0) {
    $c = Wait-Line $ClientMirror 'RX status=Pending code=(\d{6})' 30 $CAfter
    $s = Wait-Line $ServerMirror 'PENDING new terminal .*pairingCode=(\d{6})' 30 $SAfter
    if (-not $c -or -not $s) { throw "no pairing code (client=$($c.Line) server=$($s.Line))" }
    $clientCode = ([regex]::Match($c.Line, 'code=(\d{6})')).Groups[1].Value
    $serverCode = ([regex]::Match($s.Line, 'pairingCode=(\d{6})')).Groups[1].Value
    Send-Line 'server' "confirm $serverCode $Vehicle"
    $accepted = Wait-Line $ClientMirror "RX status=Accepted vehicle=$Vehicle" 20 $CAfter
    return [pscustomobject]@{ ClientCode = $clientCode; ServerCode = $serverCode; Accepted = $accepted }
}

function M([string] $Name) { Join-Path $Run "mirror/$Name.log" }

$script:Report = $null
function Start-Result([string] $Title) {
    $script:Report = [Collections.Generic.List[string]]::new()
    $script:Report.Add("# $Title")
    $script:Report.Add('')
    $script:Report.Add("运行时刻：$(Get-Date -Format 'yyyy-MM-ddTHH:mm:sszzz')；服务端 $ServerAlias（${ServerBind}:${Port}），车载电脑 $ClientAlias。")
    $script:Report.Add('')
}
function Add-Result([string] $Text) { $script:Report.Add($Text) }
function Add-Excerpt([string] $Caption, [string[]] $Lines) {
    $script:Report.Add($Caption); $script:Report.Add(''); $script:Report.Add('```text')
    foreach ($l in $Lines) { $script:Report.Add($l) }
    $script:Report.Add('```'); $script:Report.Add('')
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
    # Step 1 together with 8(a)/(b): a brand-new terminal.
    Reset-Client 'client-a'
    Invoke-Remote $ServerAlias "if (Test-Path '$ServerRoot/data/terminals.json') { Remove-Item -Force '$ServerRoot/data/terminals.json' }" | Out-Null
    Start-Server 'server' 'step1-server.log'
    $script:Pin = Get-Pin
    Start-Client 'client-a' 'client-a' 'step1-client-a.log'
    $start = Wait-Line (M 'client-a') 'START client' 60
    $up = Wait-Line (M 'client-a') 'STREAM up after' 60
    $pair = Confirm-Pending (M 'server') (M 'client-a') 'agv-a'
    $echo = Wait-Line (M 'server') 'ECHO s\d+ command=' 30
    Start-Result '第 1 步：启动与连接（并含第 8 步 a、b）'
    Add-Result "- 客户端进程启动到双向流建立（车载电脑时钟）：$([int](($up.Time - $start.Time).TotalMilliseconds)) ms（含首次生成密钥和证书）。"
    Add-Result "- 配对码：客户端 $($pair.ClientCode)，服务端 $($pair.ServerCode)，一致：$($pair.ClientCode -eq $pair.ServerCode)；确认后已接受：$([bool]$pair.Accepted)；命令回显：$([bool]$echo)。"
    Add-Excerpt '服务端' (Select-Log (M 'server') 'START|SERVER-CERT|KESTREL|CALL v1|PENDING|ADMIT|CONFIRM|ECHO' | Select-Object -First 9)
    Add-Excerpt '服务端：确认前的上报' (Select-Log (M 'server') 'RX s\d+ .*state=Pending' | Select-Object -First 3)
    Add-Excerpt '客户端' (Select-Log (M 'client-a') 'START|IDENTITY|CLIENT|CONNECT|STREAM up|RX status' | Select-Object -First 8)
    Save-Result 'step1'
}

function Step-3 {
    if (-not $script:Procs['client-a']) { throw 'run step 1 first in the same invocation' }
    $sAfter = Get-LineCount (M 'server'); $cAfter = Get-LineCount (M 'client-a')
    Send-Line 'server' 'quiet on'
    $aStart = Get-Date; Start-Sleep -Seconds 300; $aEnd = Get-Date
    Send-Line 'server' 'quiet off'
    Send-Line 'client-a' 'interval 9'
    $bStart = Get-Date; Start-Sleep -Seconds 300
    Send-Line 'client-a' 'interval 1'
    Start-Sleep -Seconds 3
    $ended = @(Select-Log (M 'server') 'SESSION-END' $sAfter) + @(Select-Log (M 'client-a') 'STREAM-END' $cAfter)
    $txA = @(Select-Log (M 'server') 'TX s\d+ Command' $sAfter | Where-Object { $t = Get-Stamp $_; $t -gt $aStart.AddSeconds(2) -and $t -lt $aEnd })
    $gapsB = @(Select-Log (M 'server') 'RX s\d+ seq=' $sAfter | Where-Object { (Get-Stamp $_) -gt $bStart.AddSeconds(20) -and (Get-Stamp $_) -lt $bStart.AddSeconds(298) } | ForEach-Object { if ($_ -match 'gapMs=(\d+)') { [double]$Matches[1] } })
    Start-Result '第 3 步：低流量长流'
    Add-Result "- (a) 服务端 5 分钟不下发：期间下发命令 $($txA.Count) 条。"
    Add-Result "- (b) 客户端每 9 秒上报 5 分钟：间隔最小 $(($gapsB | Measure-Object -Minimum).Minimum) ms、最大 $(($gapsB | Measure-Object -Maximum).Maximum) ms，共 $($gapsB.Count) 个。"
    Add-Result "- 期间断流（服务端 SESSION-END 与客户端 STREAM-END 合计）$($ended.Count) 次。"
    Add-Excerpt 'Kestrel 生效限值' (Select-Log (M 'server') 'KESTREL' | Select-Object -Last 1)
    if ($ended) { Add-Excerpt '断流记录' $ended }
    Save-Result 'step3'
}

function Invoke-Cut([string] $sM, [string] $cM, [string] $lM, [int] $HoldSec, [int] $Round) {
    $sAfter = Get-LineCount $sM; $cAfter = Get-LineCount $cM; $lAfter = Get-LineCount $lM
    Send-Line 'linkcut' 'cut'
    $cut = (Wait-Line $lM '^.{29} CUT' 10 $lAfter).Time
    Start-Sleep -Seconds $HoldSec
    Send-Line 'linkcut' 'restore'
    $restore = (Wait-Line $lM '^.{29} RESTORE' 10 $lAfter).Time
    $back = Wait-Line $sM 'FRESH-AGAIN' 90 $sAfter
    $stale = Wait-Line $sM 'STALE key=' 1 $sAfter
    $sEnd = Wait-Line $sM 'SESSION-END' 1 $sAfter
    $cEnd = Wait-Line $cM 'STREAM-END' 1 $cAfter
    $up = @(Select-Log $cM 'STREAM up' $cAfter) | Where-Object { (Get-Stamp $_) -ge $restore } | Select-Object -First 1
    $admit = @(Select-Log $sM 'ADMIT s\d+ .*state=Confirmed' $sAfter) | Select-Object -Last 1
    [pscustomobject]@{
        Round = $Round
        Cut = $cut.ToString('HH:mm:ss.fff')
        StaleAfterCutS = if ($stale) { [Math]::Round(($stale.Time - $cut).TotalSeconds, 2) } else { $null }
        ClientStreamEndAfterCutS = if ($cEnd) { [Math]::Round(($cEnd.Time - $cut).TotalSeconds, 1) } else { $null }
        ServerSessionEndAfterCutS = if ($sEnd) { [Math]::Round(($sEnd.Time - $cut).TotalSeconds, 1) } else { $null }
        ClientUpAfterRestoreS = if ($up) { [Math]::Round(((Get-Stamp $up) - $restore).TotalSeconds, 1) } else { $null }
        FreshAgainAfterRestoreS = if ($back) { [Math]::Round(($back.Time - $restore).TotalSeconds, 1) } else { $null }
        SameTerminal = [bool]($admit -match 'state=Confirmed vehicle=agv-a')
    }
}

function Step-4 {
    # The client goes through linkcut on the vehicle PC for this step.
    Stop-Remote 'client-a'
    Start-LinkCut 'linkcut' 'step4-linkcut.log'
    Start-Client 'client-a' 'client-a' 'step4-client-a.log' -Server "https://127.0.0.1:$RelayPort"
    $cAfter = Get-LineCount (M 'client-a')
    if (-not (Wait-Line (M 'client-a') 'RX status=Accepted' 60 $cAfter)) { throw 'client-a not accepted through linkcut' }
    Start-Sleep -Seconds 5
    $rows = foreach ($r in 1..10) {
        $row = Invoke-Cut (M 'server') (M 'client-a') (M 'linkcut') 60 $r
        Write-Host ($row | ConvertTo-Json -Compress)
        $row
        Start-Sleep -Seconds 5
    }
    Start-Result '第 4 步：静默断链（agv03 上用 linkcut 中转静默阻断 60 秒，10 次）'
    Add-Result '车载电脑"以太网 5"所在的公用配置文件防火墙是关的，出站阻断规则不会生效，所以改为：客户端连 agv03 本机 127.0.0.1:6443 上的 linkcut，linkcut 再转发到服务端；阻断期间 linkcut 不转发任何字节、不关任何连接。阻断时刻取自 linkcut 日志（agv03 时钟，经 NTP 对 factory01），过期判定取自服务端日志（factory01 时钟）。'
    Add-Result ''
    Add-Result '| 次 | 阻断时刻 | 阻断→判过期 (s) | 阻断→客户端断流 (s) | 阻断→服务端断流 (s) | 恢复→客户端重新建流 (s) | 恢复→新鲜上报 (s) | 同一台 |'
    Add-Result '| --- | --- | --- | --- | --- | --- | --- | --- |'
    foreach ($x in $rows) { Add-Result "| $($x.Round) | $($x.Cut) | $($x.StaleAfterCutS) | $($x.ClientStreamEndAfterCutS) | $($x.ServerSessionEndAfterCutS) | $($x.ClientUpAfterRestoreS) | $($x.FreshAgainAfterRestoreS) | $($x.SameTerminal) |" }
    Add-Result ''
    Save-Result 'step4'
    Stop-Remote 'client-a'
    Stop-Remote 'linkcut'
    Start-Client 'client-a' 'client-a' 'step6-client-a.log'
    if (-not (Wait-Line (M 'client-a') 'RX status=Accepted' 60 ((Get-LineCount (M 'client-a')) - 1))) { throw 'client-a not back on a direct connection' }
}

function Step-6 {
    if (-not $script:Procs['client-a']) { throw 'client-a must be running' }
    $rows = foreach ($down in 10, 60) {
        $cAfter = Get-LineCount (M 'client-a')
        Stop-Remote 'server' -Kill
        $killed = Get-Date
        Start-Sleep -Seconds $down
        Start-Server 'server' "step6-server-down$down.log"
        $started = [DateTimeOffset]::Now
        $up = Wait-Line (M 'client-a') 'RX status=Accepted' 90 $cAfter
        $disc = Wait-Line (M 'client-a') 'STATE Disconnected|STREAM-END' 1 $cAfter
        [pscustomobject]@{
            DownSec = $down
            DisconnectedSeenAfterKillS = if ($disc) { [Math]::Round(($disc.Time - $killed).TotalSeconds, 1) } else { $null }
            ReconnectedAfterStartS = if ($up) { [Math]::Round(($up.Time - $started).TotalSeconds, 1) } else { $null }
            Attempts = @(Select-Log (M 'client-a') 'CONNECT starting' $cAfter).Count
        }
        Start-Sleep -Seconds 5
    }
    Start-Result '第 6 步：服务端重启（结束 factory01 上的探针服务端进程，停 10 秒与 60 秒各一次）'
    Add-Result '| 停机 (s) | 结束进程→客户端发现断开 (s) | 服务端就绪→客户端重新被接受 (s) | 期间连接尝试 |'
    Add-Result '| --- | --- | --- | --- |'
    foreach ($x in $rows) { Add-Result "| $($x.DownSec) | $($x.DisconnectedSeenAfterKillS) | $($x.ReconnectedAfterStartS) | $($x.Attempts) |" }
    Add-Excerpt '重启后服务端认出的终端' (Select-Log (M 'server') 'ADMIT s\d+ .*state=Confirmed' | Select-Object -Last 2)
    Save-Result 'step6'
}

function Step-7 {
    Stop-Remote 'client-a'
    $sAfter = Get-LineCount (M 'server')
    Start-Client 'client-plain' 'client-a' 'step7-client-plain.log' -Server "http://${ServerBind}:$Port"
    Start-Sleep -Seconds 15
    Stop-Remote 'client-plain'
    $plainCalls = @(Select-Log (M 'server') 'CALL v1' $sAfter).Count
    Stop-Remote 'server'
    Start-Server 'server-other' 'step7-server-other.log' -Data "$ServerRoot/data-other"
    Start-Client 'client-pinned' 'client-a' 'step7-client-pinned.log'
    Start-Sleep -Seconds 15
    Stop-Remote 'client-pinned'
    Stop-Remote 'server-other'
    Start-Result '第 7 步：TLS 与服务端身份'
    Add-Result "- (a) 明文客户端连加密端点：客户端建流 $(@(Select-Log (M 'client-plain') 'STREAM up').Count) 次，服务端收到调用 $plainCalls 次。"
    Add-Excerpt '(a) 客户端' (Select-Log (M 'client-plain') 'STREAM-END' | Select-Object -First 1)
    Add-Excerpt '(a) 服务端' (Select-Log (M 'server') 'Failed to authenticate' $sAfter | Select-Object -First 1)
    Add-Result "- (b) 服务端换证书：客户端 PIN-MISMATCH $(@(Select-Log (M 'client-pinned') 'PIN-MISMATCH').Count) 次、发出上报 $(@(Select-Log (M 'client-pinned') 'TX seq=').Count) 份（写入本地缓冲，未建立连接），服务端收到调用 $(@(Select-Log (M 'server-other') 'CALL v1').Count) 次。"
    Add-Excerpt '(b) 客户端' (Select-Log (M 'client-pinned') 'PIN-MISMATCH|STATE ServerNotTrusted' | Select-Object -First 2)
    Save-Result 'step7'
    Start-Server 'server' 'step8-server.log'
}

function Step-8 {
    Start-Result '第 8 步：每车凭据'
    # (a)(b) a second brand-new terminal.
    Reset-Client 'client-b'
    $sAfter = Get-LineCount (M 'server')
    Start-Client 'client-b' 'client-b' 'step8-client-b.log'
    Start-Sleep -Seconds 12
    $pendingRx = @(Select-Log (M 'server') 'RX s\d+ .*state=Pending' $sAfter)
    $pendingTx = @(Select-Log (M 'server') 'TX s\d+ Command' $sAfter)
    $pair = Confirm-Pending (M 'server') (M 'client-b') 'agv-b' $sAfter
    Add-Result "- (a) 配对码 客户端 $($pair.ClientCode) / 服务端 $($pair.ServerCode)，一致 $($pair.ClientCode -eq $pair.ServerCode)；确认前 12 秒收到上报 $($pendingRx.Count) 份（Pending，不处理），下发命令 $($pendingTx.Count) 条。"
    Add-Result "- (b) 确认后接受：$([bool]$pair.Accepted)。"
    # (c) an unregistered key that claims agv-a.
    Reset-Client 'client-c'
    $sAfter = Get-LineCount (M 'server')
    Start-Client 'client-c' 'client-c' 'step8-client-c.log' @('--claim-vehicle', 'agv-a')
    Start-Sleep -Seconds 10
    Stop-Remote 'client-c'
    Reset-Client 'client-c'
    Add-Result "- (c) 未登记密钥自称 agv-a：被拒 $(@(Select-Log (M 'server') 'REFUSE .*Unauthenticated' $sAfter).Count) 次，被接受 $(@(Select-Log (M 'client-c') 'RX status=Accepted').Count) 次。"
    Add-Excerpt '(c)' (Select-Log (M 'server') 'REFUSE' $sAfter | Select-Object -First 1)
    # (d)1 quick reconnect with the same key while the server still holds the old connection.
    Start-LinkCut 'linkcut' 'step8-linkcut.log'
    Start-Client 'client-a' 'client-a' 'step8-client-a.log' -Server "https://127.0.0.1:$RelayPort"
    if (-not (Wait-Line (M 'client-a') 'RX status=Accepted' 60 ((Get-LineCount (M 'client-a')) - 1))) { throw 'client-a not accepted through linkcut before step 8 (d)' }
    Start-Sleep -Seconds 4
    $sAfter = Get-LineCount (M 'server')
    $rows = foreach ($i in 1..10) {
        $sA = Get-LineCount (M 'server')
        Send-Line 'linkcut' 'orphan'
        $t = Get-Date
        $sup = Wait-Line (M 'server') 'SUPERSEDE key=' 20 $sA
        $ok = Wait-Line (M 'server') 'ADMIT s\d+ .*state=Confirmed vehicle=agv-a' 20 $sA
        [pscustomobject]@{ Round = $i; Superseded = [bool]$sup; Admitted = [bool]$ok }
        Start-Sleep -Seconds 6
    }
    Add-Result "- (d)① 断链后同钥匙快速重连 10 次（服务端旧连接仍开着）：顶替 $(@($rows | Where-Object Superseded).Count) 次、接纳 $(@($rows | Where-Object Admitted).Count) 次，告警 $(@(Select-Log (M 'server') 'ALERT' $sAfter).Count) 条，拒绝 $(@(Select-Log (M 'server') 'REFUSE' $sAfter).Count) 次。"
    # (d)2 two processes with the same key at once.
    $sAfter = Get-LineCount (M 'server')
    Start-Client 'client-a2' 'client-a' 'step8-client-a-clone.log' -Server "https://127.0.0.1:$RelayPort"
    $alert = Wait-Line (M 'server') 'ALERT clone-suspected' 40 $sAfter
    Start-Sleep -Seconds 20
    $refused = @(Select-Log (M 'server') 'ALERT clone-suspected key=.* tried to connect again' $sAfter)
    $instances = @($refused | ForEach-Object { if ($_ -match 'instance=(\w+)') { $Matches[1] } } | Sort-Object -Unique)
    Add-Result "- (d)② 两个进程同钥匙：告警 $([bool]$alert)，之后被拒的不同实例 $($instances.Count) 个。"
    Add-Excerpt '(d)②' (@($alert.Line) + @($refused | Select-Object -First 2))
    Stop-Remote 'client-a2'
    $sAfter = Get-LineCount (M 'server')
    Send-Line 'server' 'forget agv-a'
    $re = Wait-Line (M 'server') 'PENDING new terminal .*pairingCode=(\d{6})' 150 $sAfter
    if ($re) {
        Send-Line 'server' "confirm $(([regex]::Match($re.Line, 'pairingCode=(\d{6})')).Groups[1].Value) agv-a"
        $back = Wait-Line (M 'server') 'ADMIT s\d+ .*state=Confirmed vehicle=agv-a|CONFIRM key=.* agv-a' 60 $sAfter
    }
    Add-Result "- 收尾：forget 后原终端重新配对确认为 agv-a：$([bool]$back)。"
    # (e) revoke agv-b.
    $sAfter = Get-LineCount (M 'server'); $bAfter = Get-LineCount (M 'client-b')
    Send-Line 'server' 'revoke agv-b'
    Start-Sleep -Seconds 20
    Add-Result "- (e) 吊销 agv-b：在线流以 PermissionDenied 结束 $(@(Select-Log (M 'client-b') 'STREAM-END .*PermissionDenied' $bAfter).Count -gt 0)，之后重连被拒 $(@(Select-Log (M 'server') 'REFUSE .*PermissionDenied' $sAfter).Count) 次。"
    Stop-Remote 'client-b'
    Reset-Client 'client-b'
    # Back to a direct connection for the next steps.
    Stop-Remote 'client-a'
    Stop-Remote 'linkcut'
    Start-Client 'client-a' 'client-a' 'step9-client-a.log'
    Save-Result 'step8'
}

function Step-9 {
    $cAfter = Get-LineCount (M 'client-a')
    $ok = Wait-Line (M 'client-a') 'RX status=Accepted' 60 ([Math]::Max(0, $cAfter - 40))
    Start-Result '第 9 步：版本'
    Add-Result "- (a) 主版本 1：接受 $([bool]$ok)。"
    foreach ($major in 3, 0) {
        Reset-Client "client-v$major"
        $sAfter = Get-LineCount (M 'server')
        Start-Client "client-v$major" "client-v$major" "step9-client-major$major.log" @('--major', "$major")
        Start-Sleep -Seconds 120
        Stop-Remote "client-v$major"
        $times = @(Select-Log (M "client-v$major") 'CONNECT starting' | ForEach-Object { Get-Stamp $_ })
        $max = 0; foreach ($t in $times) { $n = @($times | Where-Object { $_ -ge $t -and $_ -lt $t.AddSeconds(60) }).Count; if ($n -gt $max) { $max = $n } }
        Add-Result "- (b) 主版本 ${major}：FailedPrecondition 拒绝 $(@(Select-Log (M 'server') 'REFUSE-VERSION' $sAfter).Count) 次；2 分钟尝试 $($times.Count) 次，任意 60 秒最多 $max 次。"
        Add-Excerpt "(b) 主版本 $major" (@(Select-Log (M 'server') 'REFUSE-VERSION' $sAfter | Select-Object -First 1) + @(Select-Log (M "client-v$major") 'STREAM-END' | Select-Object -First 1))
        Reset-Client "client-v$major"
    }
    $sAfter = Get-LineCount (M 'server'); $cAfter = Get-LineCount (M 'client-a')
    Send-Line 'client-a' 'extra on'
    Send-Line 'server' 'hint on'
    Start-Sleep -Seconds 30
    Send-Line 'client-a' 'extra off'
    Send-Line 'server' 'hint off'
    Add-Result "- (c) 互带未知字段 30 秒：服务端收到带字段 50 的上报 $(@(Select-Log (M 'server') 'RX s\d+ .*unknownFields=\[50\]' $sAfter).Count) 份，客户端收到带字段 50 的命令 $(@(Select-Log (M 'client-a') 'RX command=.*unknownFields=\[50\]' $cAfter).Count) 条，断流 $(@(Select-Log (M 'server') 'SESSION-END' $sAfter).Count + @(Select-Log (M 'client-a') 'STREAM-END' $cAfter).Count) 次。"
    Save-Result 'step9'
}

function Step-2 {
    if (-not $script:Procs['client-a']) { throw 'client-a must be running' }
    $sAfter = Get-LineCount (M 'server')
    $start = Get-Date
    Start-Sleep -Seconds ($SoakMinutes * 60)
    Send-Line 'server' 'stats'
    Start-Sleep -Seconds 3
    $rx = @(Select-Log (M 'server') 'RX s\d+ seq=' $sAfter)
    $gaps = @($rx | Select-Object -Skip 1 | ForEach-Object { if ($_ -match 'gapMs=(\d+)') { [double]$Matches[1] } })
    $seqs = @($rx | ForEach-Object { if ($_ -match 'seq=(\d+)') { [long]$Matches[1] } })
    $missing = 0; for ($i = 1; $i -lt $seqs.Count; $i++) { $missing += [Math]::Max(0, $seqs[$i] - $seqs[$i - 1] - 1) }
    $sent = @(Select-Log (M 'server') 'TX s\d+ Command' $sAfter).Count
    $echoed = @(Select-Log (M 'server') 'ECHO s\d+ command=' $sAfter).Count
    Start-Result "第 2 步：双向流连续 $SoakMinutes 分钟（并含第 10 步资源）"
    Add-Result "- 时段：$($start.ToString('HH:mm:ss')) 起 $SoakMinutes 分钟（本机时钟）。"
    Add-Result "- 收到上报 $($rx.Count) 份，序号缺口 $missing 个；间隔 p50 $(Get-Percentile $gaps 0.5) ms、p99 $(Get-Percentile $gaps 0.99) ms、最大 $(($gaps | Measure-Object -Maximum).Maximum) ms。"
    Add-Result "- 下发命令 $sent 条，回显 $echoed 条；断流 $(@(Select-Log (M 'server') 'SESSION-END' $sAfter).Count) 次；上报过期 $(@(Select-Log (M 'server') 'STALE key=' $sAfter).Count) 次。"
    Add-Excerpt 'stats' (Select-Log (M 'server') 'STATS' $sAfter)
    Add-Excerpt '资源（服务端，首末）' (@(Select-Log (M 'server') 'RESOURCE' $sAfter | Select-Object -First 1) + @(Select-Log (M 'server') 'RESOURCE' $sAfter | Select-Object -Last 1))
    Add-Excerpt '资源（客户端，首末）' (@(Select-Log (M 'client-a') 'RESOURCE' | Select-Object -Last 31 | Select-Object -First 1) + @(Select-Log (M 'client-a') 'RESOURCE' | Select-Object -Last 1))
    Save-Result 'step2'
}

function Step-Collect {
    foreach ($pair in @(@($ServerAlias, $ServerRoot), @($ClientAlias, $ClientRoot))) {
        $target = Join-Path $Run "logs/$($pair[0])"
        New-Item -ItemType Directory -Force $target | Out-Null
        if ($pair[0] -eq '.') { Copy-Item -Recurse -Force "$($pair[1])/logs/*" $target }
        else {
            scp -q -r "$($pair[0]):$($pair[1])/logs/*" $target
            if ($LASTEXITCODE -ne 0) { throw "scp from '$($pair[0])' failed with exit code $LASTEXITCODE" }
        }
        Write-Host "collected $(@(Get-ChildItem $target).Count) log files from $($pair[0])"
    }
}

function Step-Cleanup {
    Start-Result '第 11 步：清理核对'
    $problems = [Collections.Generic.List[string]]::new()
    # Terminal identities first, while the client executable is still there.
    $dirs = @(Invoke-Remote $ClientAlias "if (Test-Path '$ClientRoot') { Get-ChildItem '$ClientRoot' -Directory | Where-Object Name -like 'client-*' | ForEach-Object Name }")
    foreach ($d in $dirs) { Reset-Client $d.Trim() }
    # Temporary key containers the server left behind: every SERVER-CERT line in
    # the server machine's own logs, plus this machine's mirrors of the consoles.
    $remote = @(Invoke-Remote $ServerAlias "if (Test-Path '$ServerRoot/logs') { Get-ChildItem '$ServerRoot/logs' -Filter *.log | Select-String -Pattern 'keyContainer=\{[0-9A-Fa-f-]+\} machine=(True|False)' | ForEach-Object { `$_.Matches[0].Value } }")
    $mirrored = @(Get-ChildItem (Join-Path $Run 'mirror') -Filter 'server*.log' -ErrorAction SilentlyContinue | ForEach-Object { Select-Log $_.FullName 'keyContainer=\{[0-9A-Fa-f-]+\} machine=(True|False)' })
    $containers = @(@($remote) + @($mirrored) | ForEach-Object {
            $m = [regex]::Match($_, 'keyContainer=(\{[0-9A-Fa-f-]+\}) machine=(True|False)')
            if ($m.Success) { "$($m.Groups[1].Value) $($m.Groups[2].Value)" }
        } | Sort-Object -Unique)
    # Each item travels as 'name|machine' so that a single container is not
    # unrolled into its characters on the remote side.
    $list = ($containers | ForEach-Object { "'$($_ -replace ' ', '|')'" }) -join ','
    $containerScript = @"
`$p = [System.Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider
foreach (`$item in @($list)) {
    `$c = `$item -split '\|'
    `$open = `$c[1] -eq 'True' ? [System.Security.Cryptography.CngKeyOpenOptions]::MachineKey : [System.Security.Cryptography.CngKeyOpenOptions]::None
    `$before = [System.Security.Cryptography.CngKey]::Exists(`$c[0], `$p, `$open)
    if (`$before) { [System.Security.Cryptography.CngKey]::Open(`$c[0], `$p, `$open).Delete() }
    "container `$(`$c[0]) machine=`$(`$c[1]) existedBefore=`$before existsAfter=`$([System.Security.Cryptography.CngKey]::Exists(`$c[0], `$p, `$open))"
}
"@
    $deleted = @(if ($containers.Count -gt 0) { Invoke-Remote $ServerAlias $containerScript })
    Add-Result "- $ServerAlias 上服务端导入证书时建的临时密钥容器（取自 $ServerAlias 上的服务端日志和本机镜像里全部 keyContainer=，共 $($containers.Count) 个）："
    Add-Excerpt '按名删除并核对' $deleted
    if ($deleted.Count -ne $containers.Count) { $problems.Add("expected $($containers.Count) container results, got $($deleted.Count)") }
    foreach ($line in $deleted) { if ($line -notmatch 'existsAfter=False') { $problems.Add("container still exists: $line") } }
    $check = @"
"probeProcesses=`$(@(Get-Process | Where-Object { `$_.Path -like '*simple-probe*' }).Count)"
"probeCertsLocalMachine=`$(@(Get-ChildItem Cert:\LocalMachine\My | Where-Object Subject -like '*simple-probe*').Count)"
"probeCertsCurrentUser=`$(@(Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -like '*simple-probe*').Count)"
"probeTerminalKeyNames=`$(@(certutil -key -csp 'Microsoft Software Key Storage Provider' 2>&1 | Select-String 'simple-probe').Count)"
"probeFirewallRules=`$(@(Get-NetFirewallRule -DisplayName 'simple-probe*' -ErrorAction SilentlyContinue).Count)"
"@
    foreach ($pair in @(@($ServerAlias, $ServerRoot), @($ClientAlias, $ClientRoot))) {
        Invoke-Remote $pair[0] "if (Test-Path '$($pair[1])') { Remove-Item -Recurse -Force '$($pair[1])' }" | Out-Null
        $lines = @(Invoke-Remote $pair[0] ($check + "`n`"dirExists=`$(Test-Path '$($pair[1])')`""))
        Add-Excerpt "$($pair[0]) 清理后核对（probeTerminalKeyNames 只数名字带 simple-probe 的机器密钥，也就是终端钥匙；服务端临时容器名是 GUID，靠上面按名核对）" $lines
        $expected = 'probeProcesses=0', 'probeCertsLocalMachine=0', 'probeCertsCurrentUser=0', 'probeTerminalKeyNames=0', 'probeFirewallRules=0', 'dirExists=False'
        foreach ($e in $expected) { if ($lines -notcontains $e) { $problems.Add("$($pair[0]): expected $e, got: $($lines -join '; ')") } }
    }
    if ($problems.Count -gt 0) { Add-Result ''; Add-Result "**清理核对不通过：** $($problems -join ' | ')" }
    Save-Result 'cleanup'
    if ($problems.Count -gt 0) { throw "cleanup check failed: $($problems -join ' | ')" }
}

try {
    foreach ($s in $Steps) { & "Step-$s" }
}
finally {
    Stop-All
}
