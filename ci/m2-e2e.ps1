# Milestone 2 acceptance (DESIGN.md section 12, scenarios 1-3): PoolWatch runs headless while tools/Leaker creates a
# known pool problem; the generated incident reports must name the helper with the right kind of evidence.
param(
    [Parameter(Mandatory)] [ValidateSet('handles', 'files', 'orphan', 'capture')] [string]$Scenario,
    [Parameter(Mandatory)] [string]$Exe,
    [Parameter(Mandatory)] [string]$Leaker,
    [Parameter(Mandatory)] [string]$Out,
    [int]$Duration = 170
)
. "$PSScriptRoot\common.ps1"

New-Item -ItemType Directory -Force $Out | Out-Null
$work = Join-Path $env:RUNNER_TEMP "leaker-$Scenario"
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Force $work | Out-Null

$config = if ($Scenario -eq 'capture') { "$PSScriptRoot\m3-config.json" } else { "$PSScriptRoot\m2-config.json" }
$pw = Start-PoolWatch $Exe @('--headless', '--duration', "$Duration", '--config', $config, '--out', $Out)
Start-Sleep -Seconds 25   # baseline before the problem starts

$pidFile = Join-Path $work 'child.pid'
switch ($Scenario) {
    'handles' { & $Leaker leak-handles --dir $work --count 40000 --ramp 40 --hold 40 }
    # Longer ramp: the leak is still allocating if its incident has to wait for an earlier (unrelated) capture. C1: a
    # held-back capture starts autoCaptureMinMinutes (1 in m3-config) after the earlier one ENDED, not right after it.
    'capture' { & $Leaker leak-handles --dir $work --count 60000 --ramp 150 --hold 20 }
    'files'   { & $Leaker touch-files --dir $work --count 30000 --ramp 40 --hold 30 }
    'orphan'  { & $Leaker orphan --dir $work --seconds 70 --count 20000 --pid-file $pidFile }
}
Assert-True ($LASTEXITCODE -eq 0) "Leaker $Scenario exit code 0 (got $LASTEXITCODE)"
$leakerPid = $null
if ($Scenario -eq 'orphan') { $leakerPid = [int](Get-Content $pidFile) ; Write-Host "orphan child PID $leakerPid" }

$code = Wait-PoolWatch $pw ($Duration + 120)
Assert-True ($code -eq 0) "headless exit code 0 (got $code)"
Get-Content "$Out\poolwatch.log" | Select-String 'incident|tag scan|retained|failed' | ForEach-Object { Write-Host "  log: $_" }

$reports = @(Get-ChildItem "$Out\incidents\*\report.json" -ErrorAction SilentlyContinue | ForEach-Object {
    $r = Get-Content $_.FullName -Raw | ConvertFrom-Json
    $r | Add-Member -NotePropertyName dir -NotePropertyValue $_.Directory.FullName -PassThru
} | Where-Object { -not $_.severity })  # finding folders (spawn storms, ...) are not tag incidents
Assert-True ($reports.Count -ge 1) "at least one incident written ($($reports.Count))"
foreach ($r in $reports) {
    $top = $r.suspects | Select-Object -First 1
    Write-Host ("  incident {0}: {1} tag={2} fs={3} growth={4:N1} MB top={5} ({6}) release={7} retained={8}" -f $r.id, $r.kind, $r.tag, $r.fileSystemTag,
        ($r.growthBytes / 1MB), $top.image, $top.confidence, ($null -ne $r.release), ($null -ne $r.retention))
}

function Test-IncidentFiles($r) {
    foreach ($f in 'report.md', 'report.json', 'timeline.csv', 'processes.csv', 'events.csv', 'timeline.png') {
        Assert-True (Test-Path (Join-Path $r.dir $f)) "$($r.id) has $f"
    }
    $md = Get-Content (Join-Path $r.dir 'report.md') -Raw
    Assert-True ($md -match 'Draft bug report for') "$($r.id) report.md has the draft bug report"
    Assert-True ($md -match '## System') "$($r.id) report.md has system info"
    Assert-True (@(Import-Csv (Join-Path $r.dir 'timeline.csv')).Count -ge 10) "$($r.id) timeline.csv has >= 10 samples"
}

switch ($Scenario) {
    'handles' {
        # Scenario 1: File handles held -> `File` tag growth, helper top-ranked by direct charge.
        $file = @($reports | Where-Object { $_.tag -ceq 'File' -and $_.kind -ne 'Release' })
        Assert-True ($file.Count -ge 1) "a growth incident for the File tag"
        if ($file.Count -ge 1) {
            $r = $file[0]; $top = $r.suspects[0]
            Assert-True ($top.image -eq 'Leaker.exe') "File incident: top suspect is Leaker.exe (got $($top.image))"
            Assert-True ($top.direct.present -eq $true) "File incident: direct charge present"
            Assert-True ([int]$top.direct.handlesAfter - [int]$top.direct.handlesBefore -ge 15000) "File incident: File handles grew by >= 15000 ($($top.direct.handlesBefore) -> $($top.direct.handlesAfter))"
            Assert-True ($top.confidence -eq 'StronglyCorrelated') "File incident: confidence strongly correlated (got $($top.confidence))"
            Assert-True ($r.headline -match 'Leaker\.exe') "headline names Leaker.exe: $($r.headline)"
            Assert-True ($r.growthBytes -ge 8MB) ("File tag growth >= 8 MB ({0:N1} MB)" -f ($r.growthBytes / 1MB))
            Test-IncidentFiles $r
        }
    }
    'capture' {
        # Scenario 4: the confirmation trace runs during scenario 1 -> "confirmed by trace" if pool events can be parsed.
        $file = @($reports | Where-Object { $_.tag -ceq 'File' -and $_.kind -ne 'Release' })
        Assert-True ($file.Count -ge 1) "a growth incident for the File tag"
        foreach ($x in @($reports | Where-Object { $null -ne $_.capture })) {
            Write-Host ("  traced incident {0}: tags={1} poolEvents={2} top={3} ({4})" -f $x.id, ($x.capture.tags -join ','), $x.capture.poolEvents, $x.suspects[0].image, $x.suspects[0].confidence)
        }
        # Unrelated growth on the runner can alert first and take the first trace; the leak's incident is then queued
        # and traced next. Judge the traced incident whose growth the correlation already pins on Leaker.exe.
        # (Unrelated growth, e.g. Windows servicing, can also be traced and list Leaker.exe lower down: only incidents
        # whose top suspect is Leaker.exe count; File first, as it is the tag the leak drives.)
        $cap = @($reports | Where-Object { $null -ne $_.capture -and $_.suspects.Count -gt 0 -and $_.suspects[0].image -eq 'Leaker.exe' -and $_.capture.tags -ccontains $_.tag } |
            Sort-Object @{ Expression = { $_.tag -ceq 'File' }; Descending = $true }, @{ Expression = { [int64]$_.growthBytes }; Descending = $true })
        Assert-True ($cap.Count -ge 1) "a confirmation trace was attached to an incident Leaker.exe is a suspect in"
        if ($cap.Count -ge 1) {
            $r = $cap[0]; $c = $r.capture
            Write-Host "  capture: tags=$($c.tags -join ',') parsed=$($c.poolEventsParsed) poolEvents=$($c.poolEvents) stacked=$($c.stackedPoolEvents) fileEvents=$($c.fileEvents) error=$($c.error)"
            Write-Host "  library: $($c.library)"
            foreach ($n in $c.notes) { Write-Host "  note: $n" }
            foreach ($u in @($c.pool | Select-Object -First 8)) { Write-Host ("  pool: {0} ({1}) {2} net {3:N0} B, {4} allocs, frames: {5}" -f $u.image, $u.pid, $u.tag, $u.netBytes, $u.allocs, (($u.topFrames | ForEach-Object { $_.frame }) -join ', ')) }
            foreach ($f in @($c.files | Select-Object -First 5)) { Write-Host ("  files: {0} ({1}) {2}: {3} opens, {4} dirs, {5} deletes" -f $f.image, $f.pid, $f.volume, $f.creates, $f.directoryOpens, $f.deletes) }
            Assert-True ($null -eq $c.error) "capture finished without error ($($c.error))"
            $etlPath = if ($c.etl) { Join-Path $r.dir ($c.etl -replace '/', '\\') } else { $null }
            Assert-True ($null -ne $etlPath -and (Test-Path $etlPath)) "the capture's ETL is saved and report.json points to it ($($c.etl))"
            Assert-True (-not (Test-Path (Join-Path $r.dir 'wpr-temp'))) "WPR temp folder cleaned up"
            # Spike result: TraceEvent reads the raw kernel pool events with stacks on this runner, so the
            # parsed path is required, not optional.
            Assert-True ($c.poolEventsParsed -eq $true) "pool events parsed by TraceEvent ($($c.poolEvents) events)"
            Assert-True ([int64]$c.stackedPoolEvents -gt 0) "pool events carry stacks ($($c.stackedPoolEvents))"
            $mine = @($c.pool | Where-Object { $_.image -eq 'Leaker.exe' -and $_.tag -ceq $r.tag -and [int64]$_.netBytes -gt 1MB })
            Assert-True ($mine.Count -ge 1) "trace attributes > 1 MB net $($r.tag) to Leaker.exe"
            Assert-True ($mine.Count -ge 1 -and @($mine[0].topFrames).Count -ge 1) "allocating module recorded ($(@($mine[0].topFrames | ForEach-Object { $_.frame }) -join ', '))"
            # Kernel-File rides along only with deeperCapture on (m3-config.json); say so first if that is what is missing.
            Assert-True ($c.kernelFileRecorded -eq $true) "the trace recorded Kernel-File (deeperCapture is on in m3-config.json)"
            $lf = @($c.files | Where-Object { $_.image -eq 'Leaker.exe' -and [int]$_.creates -ge 1000 })
            Assert-True ($lf.Count -ge 1) "Kernel-File attributes >= 1000 opens to Leaker.exe"
            $top = $r.suspects[0]
            Assert-True ($top.image -eq 'Leaker.exe') "capture incident: top suspect is Leaker.exe (got $($top.image))"
            Assert-True ($top.confidence -eq 'ConfirmedByTrace') "capture incident: confidence confirmed by trace (got $($top.confidence))"
            # Addendum 1: the trace outranks correlation (2a), every traced process has its identity (2b), the events cover
            # the growth (2c), no "?" suspect (2d).
            Write-Host "  trace answer: $($r.traceAnswer)"
            foreach ($o in @($r.traceOwners | Select-Object -First 3)) { Write-Host ("  trace owner: {0} ({1}) net {2:N0} B in {3} processes" -f $o.owner, $o.ownerKind, $o.netBytes, $o.processes) }
            Assert-True ($r.traceAnswer -match 'The trace shows .*Leaker' -and $r.traceAnswer -match 'Started by: ') "the answer comes from the trace: $($r.traceAnswer)"
            $lk = @($r.traceOwners | ForEach-Object { $_.top } | Where-Object { $_.name -like 'Leaker.exe*' })
            Assert-True ($lk.Count -ge 1 -and $lk[0].commandLine -like '*leak-handles*' -and @($lk[0].chain).Count -ge 2) "the traced Leaker has its command line and parent chain ($($lk[0].commandLine); $(@($lk[0].chain) -join ' > '))"
            Assert-True ($lk.Count -ge 1 -and [int64]$lk[0].createTime -gt 0 -or $lk[0].resolved) "the traced process is a resolved instance (PID + create time)"
            Assert-True (@($r.suspects | Where-Object { $_.image -eq '?' -or $_.displayName -eq '?' }).Count -eq 0) "no '?' suspect"
            $md = Get-Content (Join-Path $r.dir 'report.md') -Raw
            Assert-True ($md -match '## In plain words\s+\S.*The trace shows') "report.md leads with the trace answer"
            # 2c: events from the onset minus 2 min, not from when the incident closed.
            $evs = @(Import-PwCsv (Join-Path $r.dir 'events.csv'))
            $inv = [cultureinfo]::InvariantCulture
            function ToUtc($x) { if ($x -is [datetime]) { $x.ToUniversalTime() } else { [datetime]::Parse([string]$x, $inv, 'RoundtripKind').ToUniversalTime() } }
            $onset = ToUtc $r.onsetUtc
            $alert = ToUtc $r.alerts[0].timeUtc
            $times = @($evs | Where-Object { $_.time } | ForEach-Object { [datetime]::Parse($_.time, $inv, 'RoundtripKind').ToUniversalTime() })
            $tooEarly = @($times | Where-Object { $_ -lt $onset.AddMinutes(-2).AddSeconds(-5) })
            $beforeAlert = @($times | Where-Object { $_ -le $alert })
            Assert-True ($tooEarly.Count -eq 0) "no event in events.csv before onset - 2 min ($($tooEarly.Count) of $($times.Count))"
            Assert-True ($beforeAlert.Count -ge 1) "events.csv has events from the growth itself, before the alert ($($beforeAlert.Count) of $($times.Count))"
        }
        $summary = Get-Content "$Out\summary.json" -Raw | ConvertFrom-Json
        Assert-True ([int]$summary.capturesStarted -ge 1) "a capture was started ($($summary.capturesStarted))"
        Assert-True (@($reports | Where-Object { $_.tag -ceq 'EtwB' }).Count -eq 0) "PoolWatch's own trace buffers (EtwB) did not raise an incident"
    }
    'files' {
        # Scenario 2: tens of thousands of files touched -> file-system tag growth, helper top-ranked by activity
        # correlation; a release when the tree is deleted.
        $fs = @($reports | Where-Object { $_.fileSystemTag -eq $true -and $_.kind -ne 'Release' -and $_.suspects.Count -gt 0 -and $_.suspects[0].image -eq 'Leaker.exe' })
        Assert-True ($fs.Count -ge 1) "a file-system tag growth incident with Leaker.exe as top suspect"
        $act = @($fs | Where-Object { $_.suspects[0].activity.present -eq $true })
        Assert-True ($act.Count -ge 1) "Leaker.exe ranked by activity correlation (spearman / I/O share present)"
        if ($act.Count -ge 1) {
            $s = $act[0].suspects[0].activity
            Write-Host "  activity: spearman=$($s.spearman) lagged=$($s.laggedSpearman) ioShare=$($s.ioShare) ops=$($s.opsDelta)"
            Test-IncidentFiles $act[0]
        }
        $rel = @($reports | Where-Object { $_.fileSystemTag -eq $true -and $null -ne $_.release })
        Assert-True ($rel.Count -ge 1) "a release recorded on a file-system tag after the tree was deleted"
        if ($rel.Count -ge 1) {
            Write-Host ("  release: {0} dropped {1:N1} MB; active: {2}" -f $rel[0].tag, (($rel[0].release.fromBytes - $rel[0].release.toBytes) / 1MB), ($rel[0].release.activeProcesses -join '; '))
            Assert-True (@($rel | Where-Object { ($_.release.activeProcesses -join ' ') -match 'Leaker\.exe' }).Count -ge 1) "release names Leaker.exe among the busy processes"
        }
    }
    'orphan' {
        # Scenario 3: a child keeps doing I/O after its parent exits -> flagged as an orphan.
        $events = Import-PwCsv "$Out\events.csv"
        Assert-True (@($events | Where-Object { $_.kind -eq 'Orphaned' -and $_.pid -eq "$leakerPid" }).Count -ge 1) "events.csv has Orphaned for child $leakerPid"
        $hits = @($reports | ForEach-Object { $_.suspects } | Where-Object { $_.pid -eq $leakerPid })
        Assert-True ($hits.Count -ge 1) "the orphaned child is a suspect in an incident"
        $orph = @($hits | Where-Object { $_.isOrphan -eq $true -and $_.lifecycle.orphanActive -eq $true })
        Assert-True ($orph.Count -ge 1) "the child is flagged as an orphan with I/O (lifecycle signal)"
        $topHit = @($reports | Where-Object { $_.suspects.Count -gt 0 -and $_.suspects[0].pid -eq $leakerPid })
        Assert-True ($topHit.Count -ge 1) "the orphaned child is the top suspect of at least one incident"
        if ($topHit.Count -ge 1) {
            Assert-True ($topHit[0].headline -match 'orphaned') "headline says orphaned: $($topHit[0].headline)"
            Test-IncidentFiles $topHit[0]
        }
    }
}

Assert-True (Test-Path "$Out\since-boot.md") "since-boot.md written"
$summary = Get-Content "$Out\summary.json" -Raw | ConvertFrom-Json
Write-Host "summary: samples=$($summary.samples) alerts=$($summary.alerts) poolTagTxt=$($summary.poolTagTxt)"
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
Complete-Assertions "M2 e2e ($Scenario)"
