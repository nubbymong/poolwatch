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
# settles by about 13 minutes) and soak-config.json's shortened in-memory windows are full. Every judged figure uses
# this one window; with fewer than 3 samples in it nothing is judged and the soak fails (asserted below).
$steadyFrom = 900
$steady = @($rows | Where-Object { $_.elapsed -ge $steadyFrom })
$cpuPct = 0; $privSlope = 0; $handleSlope = 0
if ($steady.Count -ge 3) {
    $a = $steady[0]; $b = $steady[-1]
    $cpuPct = ($b.cpuSeconds - $a.cpuSeconds) / ($b.elapsed - $a.elapsed) * 100
    $xs = @($steady | ForEach-Object { $_.elapsed / 3600.0 })
    $privSlope = Slope $xs @($steady | ForEach-Object { $_.privateMB })
    $handleSlope = Slope $xs @($steady | ForEach-Object { $_.handles })
}
# Private memory flat, judged through the floors after PoolWatch's own trims (a compacting full collection and
# EmptyWorkingSet every 5 minutes). Between trims private memory ramps by a few MB and resets, so a slope over every
# row depends on where the window cuts that sawtooth (it is reported, not judged); the floor right after each trim is
# what a leak raises. A trim shows as the working set falling below 60% of the row before, and the floor is that row's
# private memory. At least 5 floors are needed (a 45-minute soak judged from 15 minutes has about 6); fewer fails
# rather than pass on too little data.
$floors = @(for ($i = 1; $i -lt $rows.Count; $i++) { if ($rows[$i].elapsed -ge $steadyFrom -and $rows[$i].workingSetMB -lt 0.6 * $rows[$i - 1].workingSetMB) { $rows[$i] } })
$floorSlope = if ($floors.Count -ge 5) { Slope @($floors | ForEach-Object { $_.elapsed / 3600.0 }) @($floors | ForEach-Object { $_.privateMB }) } else { $null }
$floorList = ($floors | ForEach-Object { '{0} s: {1} MB' -f $_.elapsed, $_.privateMB }) -join ', '
$floorText = if ($null -ne $floorSlope) { '{0:N1} MB/h through {1} floors' -f $floorSlope, $floors.Count } else { "n/a (only $($floors.Count) floors from 15 min; 5 needed)" }
# The working-set budget is a steady-state one (DESIGN.md section 3), judged over the same window as CPU and the trends.
# The whole-run maximum includes start-up (JIT, and the runtime and framework images loading before PoolWatch's first
# working-set trim) and is reported as the start-up peak, not judged. No sample in the window: nothing to judge, and
# the assertion below fails rather than pass on an empty window.
$startupPeakWs = ($rows | Measure-Object workingSetMB -Maximum).Maximum
$steadyMaxWs = if ($steady.Count -gt 0) { ($steady | Measure-Object workingSetMB -Maximum).Maximum } else { $null }
$steadyWsText = if ($null -ne $steadyMaxWs) { '{0:N1} MB' -f $steadyMaxWs } else { 'n/a (no sample from 15 min)' }
$profile = Get-Content "$Out\profile.json" -Raw | ConvertFrom-Json
$overhead = Get-Content "$Out\overhead.json" -Raw | ConvertFrom-Json
$summary = Get-Content "$Out\summary.json" -Raw | ConvertFrom-Json
$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("# Soak under load ($(if ($Ui) { 'UI, window open' } else { 'headless' }), $Minutes min, $ChainsPerMinute chains/min, $FilesPerSecond files/s, $Handles handles held)")
[void]$md.AppendLine()
[void]$md.AppendLine(("- CPU (steady, from 15 min, from outside): {0:N3}% of one core; PoolWatch's own meter: {1:N3}%" -f $cpuPct, $overhead.cpuPercentOfOneCore))
[void]$md.AppendLine(("- Working set max (steady, from 15 min): {0}; handle trend {1:N0}/h" -f $steadyWsText, $handleSlope))
[void]$md.AppendLine(("- Private memory trend through the post-trim floors (judged): {0}; over every steady row (not judged): {1:N1} MB/h" -f $floorText, $privSlope))
[void]$md.AppendLine(("- Post-trim floors: {0}" -f $(if ($floorList) { $floorList } else { 'none' })))
[void]$md.AppendLine(("- Start-up peak (working set max over the whole run, start-up included; not judged): {0:N1} MB" -f $startupPeakWs))
[void]$md.AppendLine(("- Disk writes {0:N2} MB/h (own meter); ETW events {1}; processes tracked {2}" -f ($overhead.diskWriteBytesPerHour / 1MB), $summary.etwEvents, $summary.processesTracked))
[void]$md.AppendLine(("- GC: heap {0} MB, committed {1} MB, allocated {2} MB total, gen0/1/2 {3}/{4}/{5}, server={6}" -f $profile.gc.heapMB, $profile.gc.committedMB, $profile.gc.totalAllocatedMB, $profile.gc.gen0, $profile.gc.gen1, $profile.gc.gen2, $profile.gc.server))
[void]$md.AppendLine(("- Held: {0}" -f ($profile.held | ConvertTo-Json -Compress)))
[void]$md.AppendLine()
[void]$md.AppendLine("| Stage | Calls | CPU ms | CPU % of one core | Wall ms | Max wall ms | Alloc MB | KB/call |")
[void]$md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|")
foreach ($s in $profile.stages) { [void]$md.AppendLine("| $($s.stage) | $($s.count) | $($s.cpuMs) | $($s.cpuPercentOfOneCore) | $($s.wallMs) | $($s.maxWallMs) | $($s.allocMB) | $($s.allocKBPerCall) |") }
[void]$md.AppendLine()
# -Ui: the window walks its two pages once a minute (MainForm.StartSoakCycle), each refresh profiled as ui.page<index>.
$uiPages = @($profile.stages | Where-Object { $_.stage -like 'ui.page*' } | ForEach-Object { $_.stage })
if ($Ui) {
    [void]$md.AppendLine("UI pages: ui.page0 = Pool causes (top row selected), ui.page1 = Services; refreshed: $($uiPages -join ', ')")
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
    Assert-True ($null -ne $floorSlope -and $floorSlope -lt 5) ("private memory flat: trend {0} < 5 MB/h" -f $floorText)
    Assert-True ([int]$profile.held.registryRecords -le 20000) "registry bounded ($($profile.held.registryRecords) <= 20000)"
    Assert-True ($handleSlope -lt 50) ("own handles flat: trend {0:N0}/h < 50/h" -f $handleSlope)
    if ($Ui) {
        Assert-True ('ui.page0' -in $uiPages -and 'ui.page1' -in $uiPages) "the UI soak refreshed both pages, Pool causes and Services ($($uiPages -join ', '))"
        Assert-True (@($uiPages | Where-Object { $_ -notin 'ui.page0', 'ui.page1' }).Count -eq 0) "no page beyond the two tabs was refreshed ($($uiPages -join ', '))"
    }
}
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
Complete-Assertions 'Soak'
