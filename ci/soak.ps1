# Overhead under load (an early build reported 4.8% CPU, 342 MB, 85 MB/h on a busy PC; CI idle showed 0.39%).
# Runs PoolWatch headless at the default settings while tools/Leaker generates a busy developer PC: Git Bash command
# chains (hundreds of short-lived processes per minute), file churn and a large system handle table. Samples
# PoolWatch's CPU, memory, handles and threads from outside every 30 s; writes soak.csv and soak.md next to
# PoolWatch's own profile.json (per-stage CPU / allocations, writes per file).
param(
    [Parameter(Mandatory)] [string]$Exe,
    [Parameter(Mandatory)] [string]$Leaker,
    [Parameter(Mandatory)] [string]$Out,
    [int]$Minutes = 45,
    [int]$ChainsPerMinute = 90,
    [int]$FilesPerSecond = 200,
    [int]$Handles = 300000,
    [switch]$Assert,
    [switch]$Ui
)
. "$PSScriptRoot\common.ps1"

New-Item -ItemType Directory -Force $Out | Out-Null
$work = Join-Path $env:RUNNER_TEMP 'soak-work'
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Force $work | Out-Null
$seconds = $Minutes * 60
# -Ui: the tray app with its window open the whole time (the usual desktop case), screenshots at the end.
$cfg = Join-Path $PSScriptRoot 'soak-config.json'
$pw = if ($Ui) { Start-PoolWatch $Exe @('--ui-smoke', "$seconds", '--config', $cfg, '--out', $Out) } else { Start-PoolWatch $Exe @('--headless', '--duration', "$seconds", '--config', $cfg, '--out', $Out) }
Start-Sleep -Seconds 20
$load = Start-Process -FilePath $Leaker -ArgumentList @('shell-load', '--dir', $work, '--seconds', "$($seconds - 60)", '--chains-per-minute', "$ChainsPerMinute",
    '--files-per-second', "$FilesPerSecond", '--handles', "$Handles") -PassThru -NoNewWindow
$rows = New-Object System.Collections.Generic.List[object]
$t0 = Get-Date
# PoolWatch's own clock (the floor seconds in its profile.json) started with it, about this long before this one: the
# 20 s above plus the Leaker's start.
$pwAhead = ($t0 - $pw.StartTime).TotalSeconds
while (-not $pw.HasExited) {
    try {
        $p = Get-Process -Id $pw.Id -ErrorAction Stop
        $rows.Add([pscustomobject]@{
            elapsed = [math]::Round(((Get-Date) - $t0).TotalSeconds, 0)
            cpuSeconds = [math]::Round($p.TotalProcessorTime.TotalSeconds, 2)
            workingSetMB = [math]::Round($p.WorkingSet64 / 1MB, 1)
            privateMB = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
            handles = $p.HandleCount
            threads = $p.Threads.Count
            systemHandles = $null
        })
    } catch { }
    Start-Sleep -Seconds 30
}
$code = Wait-PoolWatch $pw 180
if (-not $load.HasExited) { $load.Kill() }
$rows | Export-Csv -NoTypeInformation "$Out\soak.csv"
Assert-True ($code -eq 0) "headless exit code 0 (got $code)"

function Slope($xs, $ys) {
    $n = $xs.Count; if ($n -lt 3) { return 0 }
    $mx = ($xs | Measure-Object -Average).Average; $my = ($ys | Measure-Object -Average).Average
    $num = 0; $den = 0
    for ($i = 0; $i -lt $n; $i++) { $num += ($xs[$i] - $mx) * ($ys[$i] - $my); $den += ($xs[$i] - $mx) * ($xs[$i] - $mx) }
    if ($den -eq 0) { return 0 }; return $num / $den
}
# Steady state: from 15 minutes on. Start-up, JIT and warm-up are over by then (with the window open, private memory
# settles by about 13 minutes) and soak-config.json's shortened in-memory windows are full. It ends 10 s before
# PoolWatch's own end of run, moved onto this script's clock: the UI soak then draws and checks every window (the setup
# guide scans the PC) and the headless run writes its reports, neither of which is steady state (reported, not judged).
# Every judged figure uses this one window; with fewer than 3 samples in it nothing is judged and the soak fails
# (asserted below).
$steadyFrom = 900
$steadyTo = $seconds - $pwAhead - 10
$steady = @($rows | Where-Object { $_.elapsed -ge $steadyFrom -and $_.elapsed -le $steadyTo })
$endRows = @($rows | Where-Object { $_.elapsed -gt $steadyTo })
$cpuPct = 0; $privSlope = 0; $handleSlope = 0
if ($steady.Count -ge 3) {
    $a = $steady[0]; $b = $steady[-1]
    $cpuPct = ($b.cpuSeconds - $a.cpuSeconds) / ($b.elapsed - $a.elapsed) * 100
    $xs = @($steady | ForEach-Object { $_.elapsed / 3600.0 })
    $privSlope = Slope $xs @($steady | ForEach-Object { $_.privateMB })
    $handleSlope = Slope $xs @($steady | ForEach-Object { $_.handles })
}
$profile = Get-Content "$Out\profile.json" -Raw | ConvertFrom-Json
# Memory flat, judged through PoolWatch's own floors: profile.json memory.floors, the heap each full collection left,
# moved onto this script's clock. A floor with private memory is one of PoolWatch's own compactions, which after
# start-up run only once the heap has grown 32 MB past the last full collection; one without is a full collection the
# GC ran on its own, seen at the next 5-minute check, so both kinds can share one check. Private memory ramps and falls
# back between collections, so a slope over every row depends on where the window cuts that sawtooth (reported, not
# judged); the heap a full collection leaves is what a leak raises. Its trend is judged when at least 3 floors from 15
# minutes span 15 minutes or more. Either way private memory's maximum over the steady window, sampled from outside, is
# judged against the same 100 MB as the working set, so memory outside the managed heap cannot grow unseen.
$floors = @($profile.memory.floors | ForEach-Object {
        [pscustomobject]@{ elapsed = [math]::Round($_.seconds - $pwAhead); heapMB = $_.heapMB; kind = if ($null -ne $_.privateMB) { 'compaction' } else { 'GC' } }
    } | Where-Object { $_.elapsed -ge $steadyFrom -and $_.elapsed -le $steadyTo })
$floorSpan = if ($floors.Count -gt 0) { $floors[-1].elapsed - $floors[0].elapsed } else { 0 }
$floorSlope = if ($floors.Count -ge 3 -and $floorSpan -ge 900) { Slope @($floors | ForEach-Object { $_.elapsed / 3600.0 }) @($floors | ForEach-Object { $_.heapMB }) } else { $null }
$floorList = ($floors | ForEach-Object { '{0} s: {1} MB ({2})' -f $_.elapsed, $_.heapMB, $_.kind }) -join ', '
$compactions = @($floors | Where-Object { $_.kind -eq 'compaction' }).Count
$steadyMaxPriv = if ($steady.Count -gt 0) { ($steady | Measure-Object privateMB -Maximum).Maximum } else { $null }
$steadyPrivText = if ($null -ne $steadyMaxPriv) { '{0:N1} MB' -f $steadyMaxPriv } else { 'n/a (no sample from 15 min)' }
$floorCounts = '{0} floors from 15 min over {1:N0} min' -f $floors.Count, ($floorSpan / 60)
$privateUnder = $null -ne $steadyMaxPriv -and $steadyMaxPriv -lt 100
if ($null -ne $floorSlope) {
    $memoryFlat = $floorSlope -lt 5 -and $privateUnder
    $memoryText = 'heap trend {0:N1} MB/h through {1} < 5 MB/h, and private memory max {2} < 100 MB' -f $floorSlope, $floorCounts, $steadyPrivText
} else {
    $memoryFlat = $privateUnder
    $heapText = if ($compactions -eq 0) { 'no compaction: the heap stayed within 32 MB of its last full collection at every check' } else { "PoolWatch's own compactions among them: $compactions" }
    $memoryText = "private memory max $steadyPrivText < 100 MB (no heap trend: $floorCounts, 3 over 15 min needed; $heapText)"
}
# The working-set budget is a steady-state one (DESIGN.md section 3), judged over the same window as CPU and the trends.
# The whole-run maximum includes start-up (JIT, and the runtime and framework images loading before PoolWatch's first
# working-set trim) and is reported as the start-up peak, not judged. No sample in the window: nothing to judge, and
# the assertion below fails rather than pass on an empty window.
$startupPeakWs = ($rows | Measure-Object workingSetMB -Maximum).Maximum
$steadyMaxWs = if ($steady.Count -gt 0) { ($steady | Measure-Object workingSetMB -Maximum).Maximum } else { $null }
$steadyWsText = if ($null -ne $steadyMaxWs) { '{0:N1} MB' -f $steadyMaxWs } else { 'n/a (no sample from 15 min)' }
$overhead = Get-Content "$Out\overhead.json" -Raw | ConvertFrom-Json
$summary = Get-Content "$Out\summary.json" -Raw | ConvertFrom-Json
$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("# Soak under load ($(if ($Ui) { 'UI, window open' } else { 'headless' }), $Minutes min, $ChainsPerMinute chains/min, $FilesPerSecond files/s, $Handles handles held)")
[void]$md.AppendLine()
[void]$md.AppendLine(("- CPU (steady, from 15 min, from outside): {0:N3}% of one core; PoolWatch's own meter: {1:N3}%" -f $cpuPct, $overhead.cpuPercentOfOneCore))
[void]$md.AppendLine(("- Working set max (steady, from 15 min): {0}; handle trend {1:N0}/h" -f $steadyWsText, $handleSlope))
[void]$md.AppendLine(("- Memory flat (judged): {0}" -f $memoryText))
[void]$md.AppendLine(("- Private memory (steady, from 15 min, from outside): max {0}; trend over every row {1:N1} MB/h (not judged)" -f $steadyPrivText, $privSlope))
[void]$md.AppendLine(("- Heap after each full collection, from 15 min (PoolWatch's own floors): {0}" -f $(if ($floorList) { $floorList } else { 'none' })))
[void]$md.AppendLine(("- Start-up peak (working set max over the whole run, start-up included; not judged): {0:N1} MB" -f $startupPeakWs))
$endText = if ($endRows.Count -gt 0) { 'working set max {0:N1} MB, private memory max {1:N1} MB over {2} samples' -f ($endRows | Measure-Object workingSetMB -Maximum).Maximum, ($endRows | Measure-Object privateMB -Maximum).Maximum, $endRows.Count } else { 'no sample' }
[void]$md.AppendLine(("- End of run (after {0:N1} min: {1}; not judged): {2}" -f ($steadyTo / 60), $(if ($Ui) { "the end-of-run screenshots and checks" } else { "the reports written at exit" }), $endText))
[void]$md.AppendLine(("- Disk writes {0:N2} MB/h (own meter); ETW events {1}; processes tracked {2}" -f ($overhead.diskWriteBytesPerHour / 1MB), $summary.etwEvents, $summary.processesTracked))
[void]$md.AppendLine(("- GC: heap {0} MB, committed {1} MB, allocated {2} MB total, gen0/1/2 {3}/{4}/{5}, server={6}" -f $profile.gc.heapMB, $profile.gc.committedMB, $profile.gc.totalAllocatedMB, $profile.gc.gen0, $profile.gc.gen1, $profile.gc.gen2, $profile.gc.server))
[void]$md.AppendLine(("- Held: {0}" -f ($profile.held | ConvertTo-Json -Compress)))
[void]$md.AppendLine()
[void]$md.AppendLine("| Stage | Calls | CPU ms | CPU % of one core | Wall ms | Max wall ms | Alloc MB | KB/call |")
[void]$md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|")
foreach ($s in $profile.stages) { [void]$md.AppendLine("| $($s.stage) | $($s.count) | $($s.cpuMs) | $($s.cpuPercentOfOneCore) | $($s.wallMs) | $($s.maxWallMs) | $($s.allocMB) | $($s.allocKBPerCall) |") }
[void]$md.AppendLine()
# -Ui: the window walks the Simple view and its two tabs once a minute (MainForm.StartSoakCycle), each refresh profiled
# as ui.simple or ui.page<index>.
$uiPages = @($profile.stages | Where-Object { $_.stage -eq 'ui.simple' -or $_.stage -like 'ui.page*' } | ForEach-Object { $_.stage })
if ($Ui) {
    [void]$md.AppendLine("UI pages: ui.simple = the Simple view, ui.page0 = Pool causes (top row selected), ui.page1 = Services; refreshed: $($uiPages -join ', ')")
    [void]$md.AppendLine()
}
[void]$md.AppendLine("| File | MB | MB/h |")
[void]$md.AppendLine("|---|---:|---:|")
foreach ($w in $profile.writesByFile) { [void]$md.AppendLine("| $($w.file) | $($w.MB) | $($w.MBPerHour) |") }
$md.ToString() | Set-Content "$Out\soak.md"
Write-Host $md.ToString()
if ($Assert) {
    Assert-True ($steady.Count -ge 3) ("the steady window (from 15 min) has >= 3 samples ({0})" -f $steady.Count)
    Assert-True ($cpuPct -lt 0.5) ("CPU {0:N3}% of one core < 0.5%" -f $cpuPct)
    Assert-True ($null -ne $steadyMaxWs -and $steadyMaxWs -lt 100) ("working set max over the steady window (from 15 min, {0} samples) {1} < 100 MB" -f $steady.Count, $steadyWsText)
    Assert-True ($overhead.diskWriteBytesPerHour -lt 5MB) ("disk writes {0:N2} MB/h < 5 MB/h" -f ($overhead.diskWriteBytesPerHour / 1MB))
    Assert-True $memoryFlat "memory flat: $memoryText"
    Assert-True ([int]$profile.held.registryRecords -le 20000) "registry bounded ($($profile.held.registryRecords) <= 20000)"
    Assert-True ($handleSlope -lt 50) ("own handles flat: trend {0:N0}/h < 50/h" -f $handleSlope)
    if ($Ui) {
        Assert-True (@('ui.simple', 'ui.page0', 'ui.page1' | Where-Object { $_ -notin $uiPages }).Count -eq 0) "the UI soak refreshed the Simple view, Pool causes and Services ($($uiPages -join ', '))"
        Assert-True (@($uiPages | Where-Object { $_ -notin 'ui.simple', 'ui.page0', 'ui.page1' }).Count -eq 0) "no page beyond the Simple view and the two tabs was refreshed ($($uiPages -join ', '))"
    }
}
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
Complete-Assertions 'Soak'
