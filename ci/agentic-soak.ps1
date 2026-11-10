# The agentic-load overhead check (DESIGN.md section 3). On the owner's PC doing agentic coding, 1.0.0-beta.2 broke
# PoolWatch's own budget: 2.6% of one core, 248 MB of working set, far more than 5 MB an hour written, and thousands of
# spawn-storm folders a day. This runs PoolWatch headless at its defaults (the in-memory windows shortened, as in the
# soak, so each is full before the judged window starts) while tools/Leaker agent-load makes such a PC: a dozen sessions
# starting short Git Bash commands in rotating bursts (each pipeline stage a fork copy of its bash, spawn storms opening
# and closing every few minutes), about 600 idle processes and a Claude Code session whose transcripts PoolWatch searches;
# and every few minutes a load that grows a pool tag (File handles held, then many files touched), so growth incidents
# open beside it, as the owner's did. It samples PoolWatch from outside every 30 s and judges the steady state, from 15
# minutes on, against the hard budget: under 0.5% of one core, under 100 MB of working set, under 5 MB an hour written
# (what the report folders held back kept to their 1 MB an hour), at most 10 new spawn-storm folders an hour, and the
# growth incidents' folders made and written. It writes agentic-soak.csv and agentic-soak.md next to PoolWatch's own
# profile.json.
param(
    [Parameter(Mandatory)] [string]$Exe,
    [Parameter(Mandatory)] [string]$Leaker,
    [Parameter(Mandatory)] [string]$Out,
    [int]$Minutes = 45,
    [int]$Sessions = 12,
    [int]$StartsPerMinute = 3000,
    [int]$GrowthEveryMinutes = 7
)
. "$PSScriptRoot\common.ps1"

New-Item -ItemType Directory -Force $Out | Out-Null
$work = Join-Path $env:RUNNER_TEMP 'agentic-work'
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Force $work | Out-Null
$seconds = $Minutes * 60
$cfg = Join-Path $PSScriptRoot 'soak-agentic-config.json'
# Leaker runs its commands through Git Bash when it is there (the runner has it); without it, there are no fork copies.
$gitBash = @('C:\Program Files\Git\bin\bash.exe', 'C:\Program Files\Git\usr\bin\bash.exe') | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$pw = Start-PoolWatch $Exe @('--headless', '--duration', "$seconds", '--config', $cfg, '--out', $Out)
Start-Sleep -Seconds 20
$load = Start-Process -FilePath $Leaker -ArgumentList @('agent-load', '--dir', (Join-Path $work 'agents'), '--seconds', "$($seconds - 60)", '--sessions', "$Sessions",
    '--starts-per-minute', "$StartsPerMinute", '--idle', '600', '--transcript-mb', '30') -PassThru -NoNewWindow
$rows = New-Object System.Collections.Generic.List[object]
$t0 = Get-Date
# PoolWatch's own clock (the floor seconds in its profile.json) started with it, about this long before this one.
$pwAhead = ($t0 - $pw.StartTime).TotalSeconds
$growth = $null
$growthRuns = 0
$nextGrowth = 120
while (-not $pw.HasExited) {
    $elapsed = ((Get-Date) - $t0).TotalSeconds
    # A growth every few minutes, the one before it ended, until 5 minutes before the end: File handles held (the File
    # tag) and many files touched (the file system's tags) in turn.
    if (($null -eq $growth -or $growth.HasExited) -and $elapsed -ge $nextGrowth -and $elapsed -lt ($seconds - 300)) {
        $growthDir = Join-Path $work "growth$growthRuns"
        $growthArgs = if ($growthRuns % 2 -eq 0) { @('leak-handles', '--dir', $growthDir, '--count', '60000', '--ramp', '60', '--hold', '180') } else { @('touch-files', '--dir', $growthDir, '--count', '30000', '--ramp', '60', '--hold', '120') }
        $growth = Start-Process -FilePath $Leaker -ArgumentList $growthArgs -PassThru -NoNewWindow
        $growthRuns++
        $nextGrowth = $elapsed + $GrowthEveryMinutes * 60
    }
    try {
        $p = Get-Process -Id $pw.Id -ErrorAction Stop
        $rows.Add([pscustomobject]@{
            elapsed = [math]::Round($elapsed, 0)
            cpuSeconds = [math]::Round($p.TotalProcessorTime.TotalSeconds, 2)
            workingSetMB = [math]::Round($p.WorkingSet64 / 1MB, 1)
            privateMB = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
            handles = $p.HandleCount
        })
    } catch { }
    Start-Sleep -Seconds 30
}
$code = Wait-PoolWatch $pw 180
if (-not $load.HasExited) { $load.Kill() }
if ($null -ne $growth -and -not $growth.HasExited) { $growth.Kill() }
$rows | Export-Csv -NoTypeInformation "$Out\agentic-soak.csv"
Assert-True ($code -eq 0) "headless exit code 0 (got $code)"

# Steady state, as the soak judges it: from 15 minutes to 10 s before PoolWatch's own end of run (on this script's clock).
$steadyFrom = 900
$steadyTo = $seconds - $pwAhead - 10
$steady = @($rows | Where-Object { $_.elapsed -ge $steadyFrom -and $_.elapsed -le $steadyTo })
$cpuPct = $null
if ($steady.Count -ge 3) { $cpuPct = ($steady[-1].cpuSeconds - $steady[0].cpuSeconds) / ($steady[-1].elapsed - $steady[0].elapsed) * 100 }
$steadyMaxWs = if ($steady.Count -gt 0) { ($steady | Measure-Object workingSetMB -Maximum).Maximum } else { $null }
$profile = Get-Content "$Out\profile.json" -Raw | ConvertFrom-Json
$overhead = Get-Content "$Out\overhead.json" -Raw | ConvertFrom-Json
$summary = Get-Content "$Out\summary.json" -Raw | ConvertFrom-Json
$hours = [double]$profile.elapsedSeconds / 3600
$incidents = Join-Path $Out 'incidents'
$folders = @(if (Test-Path -LiteralPath $incidents) { Get-ChildItem -LiteralPath $incidents -Directory })
$stormFolders = @($folders | Where-Object { $_.Name -clike '*-SpawnStorm-*' })
# An incident's folder is named <yyyyMMdd-HHmmss>-<tag>-<tag in hex> (IncidentManager.IsFolderName); a finding's has its kind.
$incidentFolders = @($folders | Where-Object { $_.Name -cmatch '^[0-9]{8}-[0-9]{6}-[A-Za-z0-9_]{0,4}-[0-9a-f]{8}(-[0-9]+)?$' })
$unwritten = @($incidentFolders | Where-Object { -not (Test-Path -LiteralPath (Join-Path $_.FullName 'report.json')) -or -not (Test-Path -LiteralPath (Join-Path $_.FullName 'report.md')) })
$stormsAllowed = 10 * [math]::Max(1, [math]::Ceiling($Minutes / 60.0))
# What the budget allows the writes that could wait in the time it counted (WriteBudget.CanWaitAllowance, which its tests
# hold): its refill, the burst it starts with, and one burst more, as such a write is made with any room left and debited
# whole.
$canWaitAllowed = [double]$profile.writeBudget.canWaitAllowanceMB

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("# Agentic-load overhead check ($Minutes min, $Sessions sessions, $StartsPerMinute starts/min asked, growth every $GrowthEveryMinutes min)")
[void]$md.AppendLine()
[void]$md.AppendLine(("- Load: {0:N0} process starts seen, {1:N0} fork copies (not counted as launches), Git Bash {2}; {3} growth runs; ETW events {4:N0} ({5:N0} lost), alerts {6}" -f [double]$profile.spawns.starts, [double]$profile.spawns.forkCopies, $(if ($gitBash) { $gitBash } else { 'not found' }), $growthRuns, [double]$summary.etwEvents, [double]$summary.etwEventsLost, $summary.alerts))
[void]$md.AppendLine(("- CPU (steady, from 15 min, from outside): {0}; PoolWatch's own meter: {1:N3}%" -f $(if ($null -ne $cpuPct) { '{0:N3}% of one core' -f $cpuPct } else { 'n/a' }), [double]$overhead.cpuPercentOfOneCore))
[void]$md.AppendLine(("- Working set max (steady, from 15 min): {0}" -f $(if ($null -ne $steadyMaxWs) { '{0:N1} MB' -f $steadyMaxWs } else { 'n/a' })))
[void]$md.AppendLine(("- Disk writes (own meter): {0:N2} MB/h; report folders: {1} MB written ({2} MB that could wait, of {3} MB allowed; {4} MB a reader needed at once), a write held back {5} times, at the end {6} open incidents' reports and {7} charts behind; handle scans not written {8}" -f ([double]$overhead.diskWriteBytesPerHour / 1MB), $profile.writeBudget.spentMB, $profile.writeBudget.canWaitMB, $profile.writeBudget.canWaitAllowanceMB, $profile.writeBudget.mustMB, $profile.writeBudget.heldBack, $profile.writeBudget.incidentsBehindNow, $profile.writeBudget.chartsBehindNow, $profile.handleScansNotWritten))
[void]$md.AppendLine(("- Report folders: {0} incidents, {1} spawn-storm folders ({2} storms got no folder past 10 an hour), {3} other" -f $incidentFolders.Count, $stormFolders.Count, $profile.spawns.stormFoldersWithheld, ($folders.Count - $incidentFolders.Count - $stormFolders.Count)))
[void]$md.AppendLine(("- GC: heap {0} MB, allocated {1} MB total, gen0/1/2 {2}/{3}/{4}" -f $profile.gc.heapMB, $profile.gc.totalAllocatedMB, $profile.gc.gen0, $profile.gc.gen1, $profile.gc.gen2))
[void]$md.AppendLine(("- Held: {0}" -f ($profile.held | ConvertTo-Json -Compress)))
[void]$md.AppendLine()
[void]$md.AppendLine("| Stage | Calls | CPU ms | CPU % of one core | Max wall ms | Alloc MB |")
[void]$md.AppendLine("|---|---:|---:|---:|---:|---:|")
foreach ($s in $profile.stages) { [void]$md.AppendLine("| $($s.stage) | $($s.count) | $($s.cpuMs) | $($s.cpuPercentOfOneCore) | $($s.maxWallMs) | $($s.allocMB) |") }
[void]$md.AppendLine()
[void]$md.AppendLine("| File | MB | MB/h |")
[void]$md.AppendLine("|---|---:|---:|")
foreach ($w in $profile.writesByFile) { [void]$md.AppendLine("| $($w.file) | $($w.MB) | $($w.MBPerHour) |") }
$md.ToString() | Set-Content "$Out\agentic-soak.md"
Write-Host $md.ToString()

# The load was the agentic one: many starts, through Git Bash, with fork copies that PoolWatch saw and did not count.
Assert-True ($null -ne $gitBash) "Git Bash is on the runner (agent-load's commands run through it, so their pipeline stages are fork copies)"
Assert-True ([double]$profile.spawns.starts -ge 500 * ($Minutes - 1)) ("PoolWatch saw {0:N0} process starts (>= 500 a minute)" -f [double]$profile.spawns.starts)
Assert-True ([double]$profile.spawns.forkCopies -gt 0) ("PoolWatch saw {0:N0} fork copies and counted none as a launch" -f [double]$profile.spawns.forkCopies)
Write-Host 'CHECK-RAN: agentic-load-ran'
Assert-True ($steady.Count -ge 3 -and $null -ne $cpuPct -and $cpuPct -lt 0.5) ("CPU over the steady window ({0} samples from 15 min) {1:N3}% of one core < 0.5%" -f $steady.Count, $cpuPct)
Write-Host 'CHECK-RAN: agentic-cpu'
Assert-True ($null -ne $steadyMaxWs -and $steadyMaxWs -lt 100) ("working set max over the steady window {0} MB < 100 MB" -f $steadyMaxWs)
Write-Host 'CHECK-RAN: agentic-working-set'
Assert-True ([double]$overhead.diskWriteBytesPerHour -lt 5MB) ("disk writes {0:N2} MB/h < 5 MB/h" -f ([double]$overhead.diskWriteBytesPerHour / 1MB))
Assert-True ($canWaitAllowed -gt 0 -and [double]$profile.writeBudget.canWaitMB -le $canWaitAllowed + 0.001) ("the report folders' writes that could wait, {0} MB, kept to what their budget allows ({1:N3} MB in {2:N2} h)" -f $profile.writeBudget.canWaitMB, $canWaitAllowed, $hours)
Write-Host 'CHECK-RAN: agentic-writes'
Assert-True ($stormFolders.Count -le $stormsAllowed) ("{0} spawn-storm folders made, at most 10 an hour ({1} in {2} min)" -f $stormFolders.Count, $stormsAllowed, $Minutes)
Write-Host 'CHECK-RAN: agentic-storm-folders'
Assert-True ($incidentFolders.Count -ge 1) ("the growth loads opened {0} incidents beside the agentic load (>= 1)" -f $incidentFolders.Count)
Assert-True ($unwritten.Count -eq 0) ("every incident folder has its report.json and report.md ({0} without: {1})" -f $unwritten.Count, (($unwritten | ForEach-Object { $_.Name }) -join ', '))
Write-Host 'CHECK-RAN: agentic-incident-folders'
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
Complete-Assertions 'Agentic-load overhead check'
