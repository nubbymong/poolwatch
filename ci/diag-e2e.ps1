# Diagnostics acceptance (the 2026-10-26 additions): PoolWatch runs headless while tools/Leaker reproduces one
# pattern from the public kernel-pool reports; the findings and their report folders must describe it.
param(
    [Parameter(Mandatory)] [ValidateSet('spawn', 'clearcache', 'tokenguard', 'services', 'identity', 'tokens', 'filters', 'shape', 'lag', 'experiment', 'versions', 'accumulation', 'bundle')] [string]$Scenario,
    [Parameter(Mandatory)] [string]$Exe,
    [Parameter(Mandatory)] [string]$Leaker,
    [Parameter(Mandatory)] [string]$Out,
    [int]$Duration = 120
)
. "$PSScriptRoot\common.ps1"

New-Item -ItemType Directory -Force $Out | Out-Null
$work = Join-Path $env:RUNNER_TEMP "diag-work-$Scenario"  # not the -Out folder: that one is uploaded
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Force $work | Out-Null

function Read-Findings { @(Get-Content "$Out\findings.jsonl" -ErrorAction SilentlyContinue | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json }) }
function Read-Reports([string]$kind) {
    @(Get-ChildItem "$Out\incidents\*-$kind*\report.json" -ErrorAction SilentlyContinue | ForEach-Object {
        $r = Get-Content $_.FullName -Raw | ConvertFrom-Json
        $r | Add-Member -NotePropertyName dir -NotePropertyValue $_.Directory.FullName -PassThru
    })
}

switch ($Scenario) {
    'spawn' {
        # A repository with no tracked files (like the stray profile .git in the reports), and a stray .git in this
        # runner's own profile (an ephemeral VM; removed again below).
        $repo = Join-Path $work 'emptyrepo'
        New-Item -ItemType Directory -Force "$repo\sub" | Out-Null
        git init -q $repo
        Assert-True ($LASTEXITCODE -eq 0) "git init $repo"
        $strayGit = Join-Path $env:USERPROFILE '.git'
        $strayCreated = -not (Test-Path $strayGit)
        if ($strayCreated) { New-Item -ItemType Directory $strayGit | Out-Null }

        $pw = Start-PoolWatch $Exe @('--headless', '--duration', "$Duration", '--config', "$PSScriptRoot\diag-config.json", '--out', $Out)
        Start-Sleep -Seconds 15
        # 150 git starts in 30 s from one parent (the reports saw 68-109 per 30 s), all short-lived.
        & $Leaker spawn --dir "$repo\sub" --exe git --args '-C {dir} status --porcelain' --count 150 --seconds 30
        Assert-True ($LASTEXITCODE -eq 0) "Leaker spawn exit code 0 (got $LASTEXITCODE)"
        $code = Wait-PoolWatch $pw ($Duration + 120)
        if ($strayCreated) { Remove-Item -Recurse -Force $strayGit }
        Assert-True ($code -eq 0) "headless exit code 0 (got $code)"
        Get-Content "$Out\poolwatch.log" | Select-String 'finding|storm|stray' | ForEach-Object { Write-Host "  log: $_" }

        $findings = Read-Findings
        foreach ($f in $findings) { Write-Host "  finding: $($f.kind) [$($f.severity)] $($f.title)" }
        $storms = @(Read-Reports 'SpawnStorm' | Where-Object { $_.data.parent -match 'Leaker\.exe' })
        Assert-True ($storms.Count -ge 1) "a SpawnStorm report folder names Leaker.exe as the parent"
        if ($storms.Count -ge 1) {
            $s = $storms[0]
            Write-Host "  storm: total=$($s.data.totalStarts) peakShortLived=$($s.data.peakShortLived) byImage=$($s.data.byImage | ConvertTo-Json -Compress)"
            foreach ($p in @($s.data.patterns | Select-Object -First 3)) { Write-Host "  pattern: $($p.count)x $($p.pattern)" }
            foreach ($g in @($s.data.gitRoots)) { Write-Host "  git root: $($g.root) count=$($g.count) tracked=$($g.trackedFiles) flags=$($g.flags -join '; ')" }
            Assert-True ([int]$s.data.byImage.'git.exe' -ge 100) "storm counted >= 100 git.exe starts ($($s.data.byImage.'git.exe'))"
            Assert-True ([int]$s.data.peakShortLived -gt 30) "peak short-lived per 30 s > 30 ($($s.data.peakShortLived))"
            Assert-True (@($s.data.patterns | Where-Object { $_.pattern -eq 'git -C <path> status --porcelain' }).Count -eq 1) "top command pattern folded to 'git -C <path> status --porcelain'"
            $root = @($s.data.gitRoots | Where-Object { $_.root -eq $repo })
            Assert-True ($root.Count -eq 1) "git root resolved to $repo (from -C)"
            Assert-True ($root.Count -eq 1 -and ($root[0].flags -contains 'no tracked files' -or $root[0].flags -contains 'no tracked files (no index)')) "git root flagged as having no tracked files"
            Assert-True (Test-Path (Join-Path $s.dir 'spawns.csv')) "spawns.csv written"
            $trace = @(Import-Csv (Join-Path $s.dir 'spawns.csv'))
            Assert-True ($trace.Count -ge 100) "spawns.csv has >= 100 rows ($($trace.Count))"
            Assert-True (@($trace | Where-Object { $_.image -eq 'git.exe' -and $_.git_root -eq $repo -and $_.work_dir_source -eq '-C' }).Count -ge 50) "spawn trace rows carry the git root and '-C' as its source"
            Assert-True ((Get-Content (Join-Path $s.dir 'report.md') -Raw) -match '## Top command-line patterns') "report.md lists the command patterns"
        }
        Assert-True (@($findings | Where-Object { $_.kind -eq 'SpawnStorm' -and $_.severity -eq 'Alert' }).Count -ge 1) "findings.jsonl has the storm alert"
        Assert-True (@($findings | Where-Object { $_.kind -eq 'GitRoot' -and $_.data.root -eq $repo }).Count -ge 1) "findings.jsonl flags the git root $repo"
        $profileRoot = [IO.Path]::GetFullPath($env:USERPROFILE)
        Assert-True (@($findings | Where-Object { $_.kind -eq 'StrayGit' -and $_.data.path -eq (Join-Path $profileRoot '.git') }).Count -ge 1) "stray .git in the user profile reported"
        $events = Import-PwCsv "$Out\events.csv"
        Assert-True (@($events | Where-Object { $_.kind -eq 'Finding' -and $_.image -eq 'SpawnStorm' }).Count -ge 1) "events.csv carries the storm finding (timeline)"
        $summary = Get-Content "$Out\summary.json" -Raw | ConvertFrom-Json
        Write-Host "  summary: spawn=$($summary.spawn | ConvertTo-Json -Compress) strayGitChecked=$(@($summary.strayGitChecked).Count)"
        Assert-True ([int]$summary.spawn.gitStarts -ge 150) "summary counts >= 150 git starts ($($summary.spawn.gitStarts))"
        Assert-True (@($summary.strayGitChecked | Where-Object { $_.where -eq 'a volume root' }).Count -ge 1) "stray .git check covered the fixed volume roots"
    }
    'identity' {
        # Item 4: node.exe is never just "node.exe". A node bundled in an app folder (like Codex's runtimes\cua_node) runs a
        # script; another node is started through WMI (WmiPrvSE > cmd > node: the requester is hidden).
        $node = (Get-Command node -ErrorAction Stop).Source
        $vendor = Join-Path $env:LOCALAPPDATA 'PoolWatchTestVendor\runtimes\cua_node'
        New-Item -ItemType Directory -Force $vendor | Out-Null
        Copy-Item $node (Join-Path $vendor 'node.exe') -Force
        $js = "const fs=require('fs');const d=process.argv[2];fs.mkdirSync(d,{recursive:true});let i=0;const t=setInterval(()=>{fs.writeFileSync(d+'/f'+(i++%50)+'.txt','x'.repeat(4096));},50);setTimeout(()=>{clearInterval(t);},150000);"
        Set-Content (Join-Path $work 'server.cjs') $js
        Set-Content (Join-Path $work 'probe.js') $js
        $pw = Start-PoolWatch $Exe @('--headless', '--duration', "$Duration", '--config', "$PSScriptRoot\diag-config.json", '--out', $Out)
        Start-Sleep -Seconds 10
        $bundled = Start-Process (Join-Path $vendor 'node.exe') -ArgumentList @((Join-Path $work 'server.cjs'), (Join-Path $work 'out-bundled')) -PassThru -WindowStyle Hidden
        $wmi = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "cmd.exe /c `"`"$node`" `"$(Join-Path $work 'probe.js')`" `"$(Join-Path $work 'out-wmi')`"`"" }
        Write-Host "  bundled node PID $($bundled.Id); WMI create returned $($wmi.ReturnValue), cmd PID $($wmi.ProcessId)"
        $code = Wait-PoolWatch $pw ($Duration + 120)
        Assert-True ($code -eq 0) "headless exit code 0 (got $code)"
        if (-not $bundled.HasExited) { $bundled.Kill() }
        Get-CimInstance Win32_Process -Filter "Name = 'node.exe'" | Where-Object { $_.CommandLine -like '*probe.js*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        $summary = Get-Content "$Out\summary.json" -Raw | ConvertFrom-Json
        $nodes = @($summary.identities | Where-Object { $_.image -eq 'node.exe' })
        foreach ($n in $nodes) { Write-Host "  identity: PID $($n.pid) $($n.identity) [owner $($n.owner), $($n.ownerKind), viaWmi=$($n.viaWmi)] path=$($n.imagePath) cl=$($n.commandLine)" }
        $b = @($nodes | Where-Object { $_.pid -eq $bundled.Id })
        Assert-True ($b.Count -eq 1) "the bundled node is listed with an identity"
        Assert-True ($b.Count -eq 1 -and $b[0].identity -eq 'node.exe · PoolWatchTestVendor cua_node (server.cjs)' -or ($b.Count -eq 1 -and $b[0].identity -like 'node.exe · PoolWatchTestVendor cua_node (server.cjs) under *')) "bundled node identified by its app folder and script: $($b[0].identity)"
        $w = @($nodes | Where-Object { $_.viaWmi -eq $true })
        Assert-True ($w.Count -ge 1) "the WMI-launched node is flagged 'launched via WMI'"
        Assert-True ($w.Count -ge 1 -and $w[0].identity -like '*probe.js*(launched via WMI)' -and $w[0].commandLine -like '*probe.js*') "WMI-launched node shows its script and command line: $($w[0].identity)"
        Assert-True (@($nodes | Where-Object { $_.identity -eq 'node.exe' }).Count -eq 0) "no node.exe is identified by its image name alone"
        Remove-Item -Recurse -Force (Split-Path (Split-Path $vendor)) -ErrorAction SilentlyContinue
    }
    'tokens' {
        # Every start leaks one token (Leaker duplicates its token per child and holds it): Toke outstanding allocations
        # must track Leaker's starts, and Leaker must be the top parent.
        $pw = Start-PoolWatch $Exe @('--headless', '--duration', "$($Duration + 40)", '--config', "$PSScriptRoot\diag-config.json", '--out', $Out)
        Start-Sleep -Seconds 15
        # Four tokens per start and sharp on / off segments: the runner's own token churn (services, WMI) is tens of
        # allocations per bucket, which buries a one-per-start signal at CI scale (seen on the first run).
        & $Leaker token-spawn --dir $work --segments '0,40,0,40,0,35,5,40,0,30,0,40' --segment-seconds 6 --tokens-per-start 4 --hold 30
        Assert-True ($LASTEXITCODE -eq 0) "Leaker token-spawn exit code 0 (got $LASTEXITCODE)"
        $code = Wait-PoolWatch $pw ($Duration + 160)
        Assert-True ($code -eq 0) "headless exit code 0 (got $code)"
        $findings = Read-Findings
        foreach ($f in $findings) { Write-Host "  finding: $($f.kind) [$($f.severity)] $($f.title)" }
        $summary = Get-Content "$Out\summary.json" -Raw | ConvertFrom-Json
        $t = $summary.tokens
        Write-Host "  tokens: $($t | ConvertTo-Json -Compress -Depth 5)"
        $tf = @($findings | Where-Object { $_.kind -eq 'TokenSpawn' -and $_.data.verdict -eq 'TracksStarts' })
        Assert-True ($tf.Count -ge 1) "a TokenSpawn finding says Toke tracks process starts"
        if ($tf.Count -ge 1) {
            $d = $tf[0].data
            Assert-True ($d.owners[0].name -eq 'Leaker') "the owner with most starts is Leaker ($($d.owners[0].name))"
            Assert-True ([double]$d.spearman -ge 0.6) "Toke vs starts Spearman >= 0.6 ($($d.spearman))"
            Assert-True ([double]$d.perStart -ge 0.3) "outstanding Toke per start >= 0.3 ($($d.perStart))"
            Assert-True ([int64]$d.tokeOutstandingDelta -ge 150) "Toke outstanding rose >= 150 ($($d.tokeOutstandingDelta))"
        }
        $tags = Import-PwCsv "$Out\tags.csv"
        foreach ($tag in 'Toke', 'SeAt') {
            Assert-True (@($tags | Where-Object { $_.tag -ceq $tag }).Count -ge 1) "tags.csv tracks $tag (token family is always tracked)"
        }
    }
    'filters' {
        # A process named like the container VM process starts and exits; a minifilter that is installed but not loaded
        # is loaded and unloaded again (FilterManager events in the System log); filter instances are listed.
        $before = (fltmc filters) -join "`n"
        Write-Host $before
        $pw = Start-PoolWatch $Exe @('--headless', '--duration', "$Duration", '--config', "$PSScriptRoot\diag-config.json", '--out', $Out)
        Start-Sleep -Seconds 15
        $zygote = Join-Path $work 'vmmemCmZygote.exe'
        Copy-Item (Join-Path (Split-Path $Leaker) '*') $work -Recurse
        Copy-Item $Leaker $zygote
        & $zygote child-io --dir "$work\z" --seconds 6 --count 30
        Assert-True ($LASTEXITCODE -eq 0) "vmmemCmZygote.exe (Leaker copy) ran"
        $loaded = $null
        foreach ($f in 'bindflt', 'wcifs', 'CldFlt', 'storqosflt', 'wcnfs', 'FileCrypt', 'Bfs', 'applockerfltr') {
            if ($before -match "(?m)^$f\s") { continue }
            # Not $out: PowerShell names are case-insensitive and $Out is the output folder.
            $fltOut = (fltmc load $f 2>&1) -join ' '
            Write-Host "  fltmc load $f -> $LASTEXITCODE $fltOut"
            if ($LASTEXITCODE -eq 0) { $loaded = $f; break }
        }
        Start-Sleep -Seconds 8
        if ($loaded) { $fltOut = (fltmc unload $loaded 2>&1) -join ' '; Write-Host "  fltmc unload $loaded -> $LASTEXITCODE $fltOut" }
        $code = Wait-PoolWatch $pw ($Duration + 120)
        Assert-True ($code -eq 0) "headless exit code 0 (got $code)"
        $findings = Read-Findings
        foreach ($f in $findings) { Write-Host "  finding: $($f.kind) [$($f.severity)] $($f.title)" }
        $summary = Get-Content "$Out\summary.json" -Raw | ConvertFrom-Json
        Write-Host "  filters: $($summary.filters | ConvertTo-Json -Compress -Depth 4)"
        Assert-True (@($findings | Where-Object { $_.kind -eq 'Container' -and $_.data.process -eq 'vmmemCmZygote' -and $_.data.action -eq 'start' }).Count -ge 1) "vmmemCmZygote start on the timeline"
        Assert-True (@($findings | Where-Object { $_.kind -eq 'Container' -and $_.data.process -eq 'vmmemCmZygote' -and $_.data.action -eq 'exit' }).Count -ge 1) "vmmemCmZygote exit on the timeline"
        Assert-True (@($findings | Where-Object { $_.kind -eq 'Container' -and $_.title -like 'Watched services at start:*' }).Count -eq 1) "watched services reported at start"
        Assert-True ($null -eq $summary.filters.instancesNote) "filter instances listed (elevated): note=$($summary.filters.instancesNote)"
        Assert-True (@($summary.filters.instances).Count -ge 1) "at least one minifilter instance ($(@($summary.filters.instances).Count))"
        Assert-True (@($summary.filters.instances | Where-Object { $_.volume -match '^[A-Z]:\\$' }).Count -ge 1) "instance volumes mapped to drive letters"
        Assert-True (@($summary.filters.eventLog | Where-Object { $_ -like 'System:*subscribed' }).Count -eq 1) "System log subscription active ($($summary.filters.eventLog -join '; '))"
        if ($loaded) {
            Assert-True (@($findings | Where-Object { $_.kind -eq 'FilterEvent' -and $_.data.filter -eq $loaded -and $_.data.action -eq 'load' }).Count -ge 1) "FilterManager load of $loaded on the timeline"
            Assert-True (@($findings | Where-Object { $_.kind -eq 'FilterEvent' -and $_.data.filter -eq $loaded -and $_.data.action -ne 'load' }).Count -ge 1) "FilterManager unload of $loaded on the timeline"
        } else {
            Write-Host "  NOTE: no installed-but-unloaded minifilter could be loaded on this runner; FilterManager live events not exercised"
            Assert-True ([int]$summary.filters.filterEvents -ge 0) "FilterManager history read"
        }
    }
    'shape' {
        # Item 8: a File handle leak shows a growing shape in its incident, and the leak shape whenever, against the rates
        # just before it, allocations rose and frees did not; the system-wide counters (commit, limit, available RAM,
        # handles, System's handles by type) are recorded.
        $pw = Start-PoolWatch $Exe @('--headless', '--duration', "$Duration", '--config', "$PSScriptRoot\diag-config.json", '--out', $Out)
        Start-Sleep -Seconds 20
        & $Leaker leak-handles --dir $work --count 40000 --ramp 40 --hold 30
        Assert-True ($LASTEXITCODE -eq 0) "Leaker leak-handles exit code 0"
        $code = Wait-PoolWatch $pw ($Duration + 120)
        Assert-True ($code -eq 0) "headless exit code 0 (got $code)"
        $file = @(Read-Reports 'File' | Where-Object { $_.tag -ceq 'File' -and $_.kind -eq 'Growth' })
        Assert-True ($file.Count -ge 1) "a File growth incident"
        if ($file.Count -ge 1) {
            $sh = $file[0].allocationShape
            Write-Host "  File shape: $($sh | ConvertTo-Json -Compress)"
            # The shape is judged against the tag's rates just before the growth: allocations up and frees not is the leak
            # shape, and it must say so. The leak frees nothing while it ramps, but a fresh runner's Windows maintenance can
            # open and close tens of thousands of files in those 40 s, or a burst just before them can lift the rates it is
            # judged against: then the tag's traffic moved under the leak, and either growing shape is right.
            $before = $sh.before
            Assert-True ($null -ne $before) "the shape was judged against the File tag's rates just before the growth"
            if ($null -ne $before) {
                if ([double]$sh.allocsPerMin -gt [double]$before.allocsPerMin -and [double]$sh.freesPerMin -le [double]$before.freesPerMin) {
                    Assert-True ($sh.shape -eq 'LeakShape') "the File leak has the leak shape: $($sh.text)"
                } else {
                    Write-Host "  note: the File tag's traffic moved around the growth (allocations $($before.allocsPerMin) -> $($sh.allocsPerMin), frees $($before.freesPerMin) -> $($sh.freesPerMin) a minute)"
                    Assert-True ($sh.shape -in 'LeakShape', 'GrowingWithChurn') "the File leak is growing: $($sh.text)"
                }
            }
            Assert-True ([double]$sh.allocsPerMin -ge 10000) "allocations per minute >= 10,000 ($($sh.allocsPerMin))"
            Assert-True ($null -ne $file[0].systemAtEnd -and [int64]$file[0].systemAtEnd.commitBytes -gt 0) "the incident records commit / available RAM / handles"
            Assert-True ((Get-Content (Join-Path $file[0].dir 'report.md') -Raw) -match 'Allocation shape during the growth') "report.md states the allocation shape"
        }
        $summary = Get-Content "$Out\summary.json" -Raw | ConvertFrom-Json
        $sys = $summary.system
        Write-Host "  system: $($sys | ConvertTo-Json -Compress)"
        Assert-True ([int64]$sys.CommitBytes -gt 0 -and [int64]$sys.CommitBytes -lt [int64]$sys.CommitLimitBytes) "commit below its limit"
        Assert-True ([int64]$sys.AvailableBytes -gt 0 -and [int]$sys.Handles -gt 10000) "available RAM and total handles recorded"
        $totals = Import-PwCsv "$Out\totals.csv"
        Assert-True (@($totals | Where-Object { [int64]$_.commit_bytes -gt 0 -and [int64]$_.system_handles -gt 0 }).Count -ge 10) "totals.csv has the system series"
        $sh = Import-PwCsv "$Out\system-handles.csv"
        Assert-True (@($sh | Where-Object { $_.type -in 'Process', 'Thread', 'Key', 'File' }).Count -ge 2) "system-handles.csv lists the System process's handles by type"
    }
    'lag' {
        # Item 9: every core busy at the highest priority makes a 15 ms sleep wake up late; the probe must see it against
        # its baseline and record the pool size at that moment. C1: the trip's CPU trace is held back while the PC is
        # saturated and starts after the burn; the probe pauses during it and for 30 s after, so the run is 60 s longer.
        $pw = Start-PoolWatch $Exe @('--headless', '--duration', "$($Duration + 60)", '--config', "$PSScriptRoot\diag-config.json", '--out', $Out)
        Start-Sleep -Seconds 40   # baseline: 6 probes at 2 s, after start-up
        & $Leaker cpu-burn --dir $work --seconds 40
        Assert-True ($LASTEXITCODE -eq 0) "Leaker cpu-burn exit code 0"
        $code = Wait-PoolWatch $pw ($Duration + 180)
        Assert-True ($code -eq 0) "headless exit code 0 (got $code)"
        $findings = Read-Findings
        foreach ($f in @($findings | Where-Object { $_.kind -eq 'Lag' })) { Write-Host "  finding: $($f.title) / $($f.details -join ' / ')" }
        $summary = Get-Content "$Out\summary.json" -Raw | ConvertFrom-Json
        Write-Host "  lag: $($summary.lag | ConvertTo-Json -Compress -Depth 4)"
        $lag = @(Import-PwCsv "$Out\lag.csv")
        Assert-True ($lag.Count -ge 30) "lag.csv has a probe every 2 s ($($lag.Count) rows)"
        Assert-True ($null -ne $summary.lag.baseline -and [double]$summary.lag.baseline.timerMs -gt 0) "a baseline was learned (timer $($summary.lag.baseline.timerMs) ms)"
        $max = ($lag | ForEach-Object { [double]$_.timer_ms } | Measure-Object -Maximum).Maximum
        Write-Host "  max timer wake-up during the burn: $max ms"
        $lf = @($findings | Where-Object { $_.kind -eq 'Lag' })
        Assert-True ($lf.Count -ge 1) "a Lag finding while every core was busy (max timer $max ms)"
        if ($lf.Count -ge 1) {
            Assert-True ([int64]$lf[0].data.nonPagedBytes -gt 0) "the finding records the non-paged pool size at lag onset ($($lf[0].data.nonPagedBytes))"
            Assert-True ($lf[0].details[0] -match 'lag starts at about .* of non-paged pool') "the finding says at which pool size lag starts"
        }
        Assert-True (@($lag | Where-Object { $_.file_ms -ne '' }).Count -ge 30) "file round-trips measured"
        # Addendum 2 C: the lag trip records a CPU-only trace next to the pool size.
        $cpuCap = @(Get-ChildItem "$Out\incidents\*\report.json" -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName -Raw | ConvertFrom-Json } |
            Where-Object { $_.kind -eq 'Capture' -and $null -ne $_.capture.cpu })
        if ($cpuCap.Count -ge 1) { Write-Host "  lag cpu: $($cpuCap[0].capture.cpu.summary)" }
        Assert-True ($lf.Count -eq 0 -or ($cpuCap.Count -ge 1 -and [int64]$cpuCap[0].capture.cpu.samples -gt 1000 -and [int64]$cpuCap[0].capture.cpu.poolBytes -gt 0)) "the lag trip recorded a CPU trace with the pool size"
    }
    'clearcache' {
        # Addendum 2; S5 (review round 2): the runner is elevated, as PoolWatch always is, so the clear runs in-process:
        # there is no setup and no SYSTEM task any more. Checked: the headless clear (measured), a clear task of your own
        # (found by what it runs, RAMMap -E..., under any name; seen, kept on a no, removed on a yes), and the removal of what an older PoolWatch's setup installed: automatically
        # at the start (review round 3, item 7), and by --remove-clear-cache (the fallback) once planted again.
        $ramMap = Join-Path $env:ProgramFiles 'Sysinternals\RAMMap64.exe'
        $ramMapCreated = -not (Test-Path $ramMap)
        $oldDir = Join-Path $env:ProgramFiles 'PoolWatch'
        $oldDirCreated = -not (Test-Path $oldDir)
        $oldDll = Join-Path $oldDir 'PoolWatch-CacheHelper.dll'
        $oldConfig = Join-Path $oldDir 'PoolWatch-CacheHelper.runtimeconfig.json'
        $installed = Join-Path $oldDir 'PoolWatch.exe' # an installed PoolWatch next to the old helper: never touched
        $installedCreated = -not (Test-Path $installed)
        $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
        $oldTasks = @(@('Clear cache (standby)', 'standby'), @('Clear cache (low priority)', 'lowpriority'))
        $plantOld = {
            # What an older build's setup left behind: the helper files and two on-demand SYSTEM tasks in \PoolWatch\.
            New-Item -ItemType Directory -Force $oldDir | Out-Null
            Set-Content -Path $oldDll -Value 'old helper placeholder'
            Set-Content -Path $oldConfig -Value '{}'
            foreach ($t in $oldTasks) {
                Register-ScheduledTask -TaskPath '\PoolWatch\' -TaskName $t[0] -Action (New-ScheduledTaskAction -Execute $dotnet -Argument "`"$oldDll`" $($t[1])") -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
            }
        }
        $oldTasksLeft = { @(Get-ScheduledTask -TaskPath '\PoolWatch\' -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like 'Clear cache*' }).Count }
        $legacyName = 'Nightly memory clean'
        $burn = $null
        try {
            if ($installedCreated) { New-Item -ItemType Directory -Force $oldDir | Out-Null; Set-Content -Path $installed -Value 'installed PoolWatch placeholder' }
            & $plantOld

            # A clear task of your own: Task Scheduler root, SYSTEM, hourly, running RAMMap -Ew (a placeholder exe here), under
            # a name PoolWatch has never heard of: it is found by what it runs.
            if ($ramMapCreated) { New-Item -ItemType Directory -Force (Split-Path $ramMap) | Out-Null; Copy-Item "$env:SystemRoot\System32\whoami.exe" $ramMap }
            $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddHours(1) -RepetitionInterval (New-TimeSpan -Hours 1)
            Register-ScheduledTask -TaskPath '\' -TaskName $legacyName -Action (New-ScheduledTaskAction -Execute $ramMap -Argument '-Ew') -Trigger $trigger -User 'SYSTEM' -RunLevel Highest -Force | Out-Null

            # Fill the standby list: write and read back 2 GB (the pages stay cached after the handles close).
            $big = Join-Path $work 'cache-fill.bin'
            $fs = [IO.File]::Create($big); $buf = New-Object byte[] (8MB); (New-Object Random 1).NextBytes($buf)
            for ($i = 0; $i -lt 256; $i++) { $fs.Write($buf, 0, $buf.Length) }; $fs.Close()
            $rs = [IO.File]::OpenRead($big); while ($rs.Read($buf, 0, $buf.Length) -gt 0) { }; $rs.Close()

            $pw = Start-PoolWatch $Exe @('--headless', '--duration', '200', '--config', "$PSScriptRoot\diag-config.json", '--out', $Out, '--clear-cache', 'standby', '--capture-now', '15')
            # A known CPU load for the check of the CPU part below: while a CPU trace records (PoolWatch has written its
            # profile, poolwatch-cpu.wprp, and not yet saved cpu.etl next to it), a job of its own keeps one processor
            # busy, for at most 120 s in all. It returns how long it was busy.
            $burn = Start-Job -ArgumentList (Join-Path $Out 'incidents'), $pw.Id -ScriptBlock {
                param([string]$incidents, [int]$watched)
                $busy = [Diagnostics.Stopwatch]::new()
                $x = 1.0
                while ((Get-Process -Id $watched -ErrorAction SilentlyContinue) -and $busy.Elapsed.TotalSeconds -lt 120) {
                    $recording = @(Get-ChildItem -Path (Join-Path $incidents '*\poolwatch-cpu.wprp') -ErrorAction SilentlyContinue |
                        Where-Object { -not (Test-Path (Join-Path $_.DirectoryName 'cpu.etl')) })
                    if ($recording.Count -eq 0) { Start-Sleep -Milliseconds 100; continue }
                    $busy.Start()
                    for ($i = 0; $i -lt 200000; $i++) { $x = [Math]::Sqrt($x + 1) }
                    $busy.Stop()
                }
                $busy.Elapsed.TotalSeconds
            }
            $code = Wait-PoolWatch $pw 400
            $burnSeconds = 0.0
            if (Wait-Job $burn -Timeout 30) {
                $burned = @(Receive-Job $burn -ErrorAction SilentlyContinue)
                if ($burned.Count -gt 0) { $burnSeconds = [double]$burned[-1] }
            }
            Assert-True ($code -eq 0) "headless exit code 0 (got $code)"
            $summary = Get-Content "$Out\summary.json" -Raw | ConvertFrom-Json
            Write-Host "  lists: $($summary.memoryLists | ConvertTo-Json -Compress)"
            Write-Host "  helper: $($summary.clearHelper | ConvertTo-Json -Compress)"
            foreach ($c in @($summary.clears)) { Write-Host "  clear: $($c.summary)" }
            Assert-True ($summary.clearHelper.CanPurge -eq $true) "the clear runs in-process (PoolWatch is elevated; no setup)"
            Assert-True ($summary.clearHelper.LegacyTaskPresent -eq $true) "PoolWatch sees your own clear task"
            # Review round 3 (item 7): the first status read removed the older build's tasks and helper files by itself.
            Assert-True (@($summary.clearHelper.OldTasks).Count -eq 0) "the older build's two tasks were removed at the start ($(@($summary.clearHelper.OldTasks) -join ', '))"
            Assert-True ((& $oldTasksLeft) -eq 0) "the old tasks are gone from Task Scheduler"
            Assert-True (-not (Test-Path $oldDll) -and -not (Test-Path $oldConfig)) "the old helper files are gone"
            Assert-True (Test-Path $installed) "an installed PoolWatch.exe next to them is left alone"
            Assert-True ($null -ne (Get-ScheduledTask -TaskPath '\' -TaskName $legacyName -ErrorAction SilentlyContinue)) "your own clear task is not touched by the automatic removal"
            $setup = @(Read-Findings | Where-Object { $_.kind -eq 'Setup' -and $_.title -like 'Clear cache: Removed *' })
            Assert-True ($setup.Count -eq 1) "one timeline note for the removal ($($setup.Count)): $($setup[0].title)"
            Assert-True ([int64]$summary.memoryLists.StandbyBytes -gt 0) "the memory lists are read (standby $($summary.memoryLists.StandbyBytes))"
            $c = @($summary.clears)
            Assert-True ($c.Count -eq 1 -and $null -eq $c[0].error) "one clear, no error ($($c[0].error))"
            if ($c.Count -eq 1) {
                Assert-True ($c[0].measured -eq $true) "the clear was measured 2 min later"
                # Right after the purge (the cache refills within the 2 minutes).
                Assert-True ([int64]$c[0].standbyBefore -ge 1GB -and $c[0].immediateKnown -eq $true -and [int64]$c[0].standbyImmediate -lt [int64]$c[0].standbyBefore / 2) "the standby list was emptied ($($c[0].standbyBefore) -> $($c[0].standbyImmediate), $($c[0].standbyAfter) 2 min later)"
            }
            $f = @(Read-Findings | Where-Object { $_.kind -eq 'CacheClear' })
            Assert-True ($f.Count -eq 1 -and $f[0].title -like 'Clear cache (standby list, command line): freed *') "a CacheClear finding with the freed amount: $($f[0].title)"
            $cap = @(Get-ChildItem "$Out\incidents\*\report.json" | ForEach-Object { Get-Content $_.FullName -Raw | ConvertFrom-Json } | Where-Object { $_.kind -eq 'Capture' })
            Assert-True ($cap.Count -ge 1) "Capture now made a capture incident"
            if ($cap.Count -ge 1) {
                $cpu = $cap[0].capture.cpu
                Write-Host "  cpu: $($cpu.summary)"
                # The CPU part must hold a real sampled profile. A fixed count cannot say so: on these VM runners an idle
                # processor records no samples at all (none of the x64 or Arm64 CPU traces checked had an idle sample), so
                # the count says how busy the runner was, not how long the trace ran: quiet runners gave 1690 samples (x64)
                # and 884 (Arm64) in the 10 s recording. With the known load above, one processor is busy for the whole
                # recording. The check asks for a quarter of one processor at the 1 ms interval over the recording length
                # this script configures (cpuTraceSeconds in diag-config.json): 2500 for 10 s. In the four CPU traces
                # checked (this scenario and lag, x64 and Arm64), the runners recorded 0.96 to 1.4 samples per busy
                # millisecond of the recording, against PoolWatch's own CPU series, so the busy processor alone should
                # give about 9600 or more: about four times the bound. A missing or broken sampled profile cannot reach
                # it. The trace's own span (cpu.seconds) is not the base: it also counts the time wpr -stop takes to save
                # the trace (1 to 5 s on these runners), when nothing is sampled.
                $traceSeconds = [double]([regex]::Match((Get-Content "$PSScriptRoot\diag-config.json" -Raw), '"cpuTraceSeconds"\s*:\s*(\d+)').Groups[1].Value)
                $need = [int64](0.25 * 1000 * $traceSeconds)
                Assert-True ($burnSeconds -gt 0) "a known CPU load ran while the CPU trace recorded ($([Math]::Round($burnSeconds, 1)) s busy)"
                Assert-True ($null -ne $cpu -and $need -gt 0 -and [int64]$cpu.samples -ge $need -and @($cpu.topCpu).Count -ge 2) "the capture has the CPU part ($($cpu.samples) samples, at least $need for one busy processor over the $traceSeconds s recording; the trace's span was $([Math]::Round([double]$cpu.seconds, 1)) s)"
                Assert-True (@($cpu.topWait).Count -ge 1) "waits by module from the context switches"
            }
            Remove-Item $big -ErrorAction SilentlyContinue
            # Turning on the automatic clear: the step finds your own clear task, shows it, removes it only on a yes.
            $no = Start-Process -FilePath $Exe -ArgumentList @('--enable-auto-clear', '--no', $script:AnyLocation) -Wait -PassThru -NoNewWindow
            Assert-True ($no.ExitCode -eq 4 -and $null -ne (Get-ScheduledTask -TaskPath '\' -TaskName $legacyName -ErrorAction SilentlyContinue)) "answering no keeps your own clear task and reports it (exit 4: the automatic clear stays off)"
            $yes = Start-Process -FilePath $Exe -ArgumentList @('--enable-auto-clear', '--yes', $script:AnyLocation) -Wait -PassThru -NoNewWindow
            Assert-True ($yes.ExitCode -eq 0 -and $null -eq (Get-ScheduledTask -TaskPath '\' -TaskName $legacyName -ErrorAction SilentlyContinue)) "answering yes removes your own clear task (exit $($yes.ExitCode))"
            Assert-True (Test-Path $ramMap) "RAMMap64.exe is left in place"
            $again = Start-Process -FilePath $Exe -ArgumentList @('--remove-legacy-clear-task', '--yes', $script:AnyLocation) -Wait -PassThru -NoNewWindow
            Assert-True ($again.ExitCode -eq 0) "with no clear task of your own the step is a no-op"

            # The fallback: planted again, --remove-clear-cache removes its two tasks and the helper files, nothing else.
            & $plantOld
            Assert-True ((& $oldTasksLeft) -eq 2) "the old tasks are planted again for the fallback"
            $rm = Start-Process -FilePath $Exe -ArgumentList @('--remove-clear-cache', $script:AnyLocation) -Wait -PassThru -NoNewWindow
            Assert-True ($rm.ExitCode -eq 0) "--remove-clear-cache exit code 0 (got $($rm.ExitCode))"
            Assert-True ((& $oldTasksLeft) -eq 0) "the old tasks are gone"
            Assert-True (-not (Test-Path $oldDll) -and -not (Test-Path $oldConfig)) "the old helper files are gone"
            Assert-True (Test-Path $installed) "an installed PoolWatch.exe next to them is left alone"
        }
        finally {
            if ($burn) { Remove-Job $burn -Force -ErrorAction SilentlyContinue }
            Unregister-ScheduledTask -TaskPath '\' -TaskName $legacyName -Confirm:$false -ErrorAction SilentlyContinue
            foreach ($t in $oldTasks) { Unregister-ScheduledTask -TaskPath '\PoolWatch\' -TaskName $t[0] -Confirm:$false -ErrorAction SilentlyContinue }
            if ($ramMapCreated) { Remove-Item -Force $ramMap -ErrorAction SilentlyContinue; Remove-Item (Split-Path $ramMap) -ErrorAction SilentlyContinue }
            Remove-Item -Force $oldDll, $oldConfig -ErrorAction SilentlyContinue
            if ($installedCreated) { Remove-Item -Force $installed -ErrorAction SilentlyContinue }
            if ($oldDirCreated) { Remove-Item $oldDir -ErrorAction SilentlyContinue }
        }
    }
    'experiment' {
        # Item 10: a guided A/B run with 1-minute phases where the "app" is Leaker holding 60,000 File handles. The script
        # plays the user: it starts Leaker at the "start" prompt and closes it (its own child) at the "close" prompt.
        $pw = Start-PoolWatch $Exe @('--headless', '--duration', '260', '--config', "$PSScriptRoot\diag-config.json", '--out', $Out,
            '--experiment', 'Leaker', '--phase-minutes', '1')
        function Wait-Prompt([string]$pattern, [int]$seconds) {
            $until = (Get-Date).AddSeconds($seconds)
            while ((Get-Date) -lt $until) {
                $hit = @(Read-Findings | Where-Object { $_.kind -eq 'Experiment' -and $_.title -match $pattern })
                if ($hit.Count -gt 0) { Write-Host "  prompt: $($hit[0].title)"; return $true }
                Start-Sleep -Milliseconds 500
            }
            return $false
        }
        Assert-True (Wait-Prompt 'Experiment started' 40) "the baseline prompt appears"
        $mark = Start-Process -FilePath $Exe -ArgumentList @('--mark', '"lag while typing"', '--out', $Out, $script:AnyLocation) -Wait -PassThru -NoNewWindow
        Assert-True ($mark.ExitCode -eq 0) "--mark exit code 0 (got $($mark.ExitCode))"
        Assert-True (Wait-Prompt 'start Leaker now' 100) "the start prompt appears after the baseline"
        $leak = Start-Process -FilePath $Leaker -ArgumentList @('leak-handles', '--dir', $work, '--count', '60000', '--ramp', '20', '--hold', '600') -PassThru -NoNewWindow
        Assert-True (Wait-Prompt 'close Leaker now' 100) "the close prompt appears after the app phase"
        if (-not $leak.HasExited) { $leak.Kill() } # the test's own child: the user closing the app
        Assert-True (Wait-Prompt 'Experiment result' 100) "the result appears after the closed phase"
        $code = Wait-PoolWatch $pw 400
        Assert-True ($code -eq 0) "headless exit code 0 (got $code)"
        $summary = Get-Content "$Out\summary.json" -Raw | ConvertFrom-Json
        Write-Host "  experiment: $($summary.experiment | ConvertTo-Json -Compress -Depth 4)"
        Assert-True ($summary.experiment.phase -eq 'Done') "the experiment finished ($($summary.experiment.phase))"
        Assert-True ($summary.experiment.statement -match 'Growth stopped when Leaker closed') "plain statement: $($summary.experiment.statement)"
        $file = @($summary.experiment.grew | Where-Object { $_.Name -eq 'File' })
        Assert-True ($file.Count -eq 1 -and [int64]$file[0].WithApp -gt 10MB -and $file[0].Stopped) "File grew with Leaker running and stopped after it closed ($($file | ConvertTo-Json -Compress))"
        $rep = @(Read-Reports 'Experiment')
        Assert-True ($rep.Count -eq 1) "one experiment report folder"
        if ($rep.Count -eq 1) {
            Assert-True (Test-Path "$($rep[0].dir)\experiment.csv") "experiment.csv with every tag per phase"
            Assert-True ((Get-Content "$($rep[0].dir)\report.md" -Raw) -match '## Per phase') "report.md has the per-phase table"
        }
        Assert-True (@($summary.marks) -contains 'Mark: lag while typing') "the --mark note is on the timeline ($($summary.marks -join '; '))"
        $ev = @(Import-PwCsv "$Out\events.csv" | Where-Object { $_.kind -eq 'Finding' -and $_.detail -like 'Mark: lag*' })
        Assert-True ($ev.Count -eq 1) "the mark is in events.csv"

        # Addendum 2 C: the preset "lag before vs after a clear" (1-minute phases; the runner is elevated, so PoolWatch
        # purges in-process at the phase boundary).
        $out2 = "$Out-clearlag"
        $pw = Start-PoolWatch $Exe @('--headless', '--duration', '170', '--config', "$PSScriptRoot\diag-config.json", '--out', $out2,
            '--experiment', '"clear cache"', '--phase-minutes', '1')
        $code = Wait-PoolWatch $pw 300
        Assert-True ($code -eq 0) "clear preset: headless exit code 0 (got $code)"
        $s2 = Get-Content "$out2\summary.json" -Raw | ConvertFrom-Json
        Write-Host "  clear preset: $($s2.clearExperiment | ConvertTo-Json -Compress -Depth 4)"
        Assert-True ($s2.clearExperiment.phase -eq 'Done' -and $null -eq $s2.clearExperiment.clearError) "the preset finished and its clear ran ($($s2.clearExperiment.phase), $($s2.clearExperiment.clearError))"
        Assert-True ($s2.clearExperiment.statement -match '^Lag (was lower after|was higher after|did not change with) the clear: timer wake-up ') "plain lag comparison: $($s2.clearExperiment.statement)"
        Assert-True (@($s2.clears | Where-Object { $_.trigger -eq 'Experiment' -and $null -eq $_.error }).Count -eq 1) "one clear with trigger Experiment"
        Copy-Item -Recurse "$out2\*" (New-Item -ItemType Directory -Force "$Out\clearlag").FullName -ErrorAction SilentlyContinue
    }
    'accumulation' {
        # Item 12: Leaker starts a long-running cmd.exe every 6 s and never ends them (like cua_node helpers piling up);
        # censuses every 10 s must flag the group under its owner.
        $cfg = Get-Content "$PSScriptRoot\diag-config.json" -Raw | ConvertFrom-Json
        foreach ($kv in @{ accumulationIntervalSeconds = 10; accumulationWindowPoints = 4; accumulationMinCount = 5; accumulationMinRise = 3 }.GetEnumerator()) {
            $cfg | Add-Member -NotePropertyName $kv.Key -NotePropertyValue $kv.Value -Force
        }
        $cfgFile = Join-Path $work 'accumulation-config.json'
        $cfg | ConvertTo-Json -Depth 5 | Set-Content $cfgFile
        $pw = Start-PoolWatch $Exe @('--headless', '--duration', '150', '--config', $cfgFile, '--out', $Out)
        Start-Sleep -Seconds 10
        & $Leaker accumulate --dir $work --count 15 --every 6 --hold 30
        Assert-True ($LASTEXITCODE -eq 0) "Leaker accumulate exit code 0"
        $code = Wait-PoolWatch $pw 300
        Assert-True ($code -eq 0) "headless exit code 0 (got $code)"
        $summary = Get-Content "$Out\summary.json" -Raw | ConvertFrom-Json
        Write-Host "  accumulation: $($summary.accumulation | ConvertTo-Json -Compress -Depth 4)"
        $f = @(Read-Findings | Where-Object { $_.kind -eq 'ChildAccumulation' })
        foreach ($x in $f) { Write-Host "  finding: $($x.title) / $($x.details -join ' / ')" }
        $cmd = @($f | Where-Object { $_.data.what -like 'cmd.exe*' -and $_.data.owner -eq 'Leaker' })
        Assert-True ($cmd.Count -ge 1) "the cmd.exe children piling up under Leaker are flagged"
        if ($cmd.Count -ge 1) {
            Assert-True ($cmd[0].title -match 'cmd\.exe · /c ping under Leaker') "the finding names the identity and the owner: $($cmd[0].title)"
            Assert-True ([int]$cmd[0].data.to -ge 5 -and [int]$cmd[0].data.to - [int]$cmd[0].data.from -ge 3) "it counts the rise ($($cmd[0].data.from) -> $($cmd[0].data.to))"
        }
        Assert-True ([int]$summary.accumulation.censuses -ge 10) "censuses every 10 s ($($summary.accumulation.censuses))"
    }
    'bundle' {
        # Item 13: two runs in one boot keep one boot summary and one baseline; --export-bundle writes the vendor zip,
        # redacted by default (no user or machine name in any file).
        $pw = Start-PoolWatch $Exe @('--headless', '--duration', '40', '--config', "$PSScriptRoot\diag-config.json", '--out', $Out)
        Start-Sleep -Seconds 10
        & $Leaker leak-handles --dir $work --count 30000 --ramp 10 --hold 15
        $code = Wait-PoolWatch $pw 120
        Assert-True ($code -eq 0) "first run exit code 0 (got $code)"
        # 75 s: the one-minute history needs a minute boundary for samples.csv rows and tags/history-start.csv.
        $pw = Start-PoolWatch $Exe @('--headless', '--duration', '75', '--config', "$PSScriptRoot\diag-config.json", '--out', $Out, '--export-bundle')
        $code = Wait-PoolWatch $pw 180
        Assert-True ($code -eq 0) "second run exit code 0 (got $code)"
        $summary = Get-Content "$Out\summary.json" -Raw | ConvertFrom-Json
        Write-Host "  boots: $($summary.boots | ConvertTo-Json -Compress); baseline: $($summary.bootBaseline | ConvertTo-Json -Compress)"
        Assert-True (@($summary.boots).Count -eq 1) "two runs in one boot are one boot summary ($(@($summary.boots).Count))"
        Assert-True (@(Get-ChildItem "$Out\boots" -Directory).Count -eq 1 -and (Test-Path "$Out\boots\*\baseline.json")) "one boot folder with its baseline"
        Assert-True ([int]$summary.bootBaseline.tags -gt 100) "the baseline is a full tag snapshot ($($summary.bootBaseline.tags) tags)"
        Assert-True (Test-Path "$Out\boots.md") "boots.md written at exit"
        $zip = $summary.vendorBundle
        Assert-True ($zip -and (Test-Path $zip) -and $zip -like '*-redacted.zip') "a redacted vendor bundle was written ($zip)"
        if ($zip -and (Test-Path $zip)) {
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $z = [System.IO.Compression.ZipFile]::OpenRead($zip)
            try {
                $names = @($z.Entries | ForEach-Object { $_.FullName })
                Write-Host "  bundle: $($names -join ', ')"
                foreach ($n in 'README.md', 'system.json', 'boot-baseline.json', 'boots.md', 'samples.csv', 'tags/after-reboot.csv', 'tags/now.csv', 'tags/history-start.csv', 'incidents.md', 'filters.json', 'git-and-spawns.md', 'crashes.md', 'findings.jsonl') {
                    Assert-True ($names -contains $n) "bundle has $n"
                }
                $leaks = @()
                foreach ($e in $z.Entries) {
                    $r = New-Object System.IO.StreamReader($e.Open()); $text = $r.ReadToEnd(); $r.Close()
                    $short = (Split-Path (New-Object -ComObject Scripting.FileSystemObject).GetFolder($env:USERPROFILE).ShortPath -Leaf)
                    foreach ($secret in @($env:USERNAME, $env:COMPUTERNAME, $short)) { if ($secret.Length -ge 3 -and $text -match [regex]::Escape($secret)) { $leaks += "$($e.FullName): $secret" } }
                }
                Assert-True ($leaks.Count -eq 0) "no user or machine name in the redacted bundle ($($leaks -join '; '))"
                $sys = (New-Object System.IO.StreamReader($z.GetEntry('system.json').Open())).ReadToEnd() | ConvertFrom-Json
                Assert-True ($sys.build -match '^\d{5}') "system.json has the Windows build ($($sys.build))"
                $samples = (New-Object System.IO.StreamReader($z.GetEntry('samples.csv').Open())).ReadToEnd() -split "`n" | Where-Object { $_ }
                Assert-True ($samples.Count -ge 2 -and $samples[0] -like 'time_utc,commit_bytes,*') "samples.csv has the minute series ($($samples.Count - 1) rows)"
            } finally { $z.Dispose() }
            Remove-Item $zip -ErrorAction SilentlyContinue
        }
    }
    'versions' {
        # Item 11: a Claude Code version appears between two runs, and a watched app (Leaker, via the config) crashes
        # while PoolWatch is not running; the second run must report both.
        $cfg = Get-Content "$PSScriptRoot\diag-config.json" -Raw | ConvertFrom-Json
        $cfg | Add-Member -NotePropertyName watchedApps -NotePropertyValue @('Leaker') -Force
        $cfgFile = Join-Path $work 'versions-config.json'
        $cfg | ConvertTo-Json -Depth 5 | Set-Content $cfgFile
        $versions = Join-Path $env:USERPROFILE '.local\share\claude\versions'
        $created = -not (Test-Path $versions)
        New-Item -ItemType Directory -Force "$versions\2.0.14" | Out-Null
        try {
            $pw = Start-PoolWatch $Exe @('--headless', '--duration', '20', '--config', $cfgFile, '--out', $Out)
            $code = Wait-PoolWatch $pw 120
            Assert-True ($code -eq 0) "first run exit code 0 (got $code)"
            $first = @(Read-Findings | Where-Object { $_.kind -eq 'VersionChange' })
            foreach ($f in $first) { Write-Host "  run 1: $($f.title)" }
            Assert-True (@($first | Where-Object { $_.title -match 'App versions: .*Claude Code 2\.0\.14' }).Count -eq 1) "first run lists the installed versions"
            Assert-True (Test-Path "$Out\cache\last-seen.txt") "the run's last-seen time is kept"

            New-Item -ItemType Directory -Force "$versions\2.0.15" | Out-Null
            $crashStart = Get-Date
            $leak = Start-Process -FilePath $Leaker -ArgumentList @('crash', '--dir', $work) -PassThru -NoNewWindow
            if (-not $leak.WaitForExit(60000)) { $leak.Kill(); Write-Host "  Leaker crash did not exit (a WER dialog?): killed" }
            else { Write-Host "  Leaker crash exit code $($leak.ExitCode)" }
            Start-Sleep -Seconds 8   # WER writes Application Error 1000 a moment after the process dies
            $logged = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'Application Error'; Id = 1000; StartTime = $crashStart } -ErrorAction SilentlyContinue |
                Where-Object { $_.Properties[0].Value -like 'Leaker*' })
            Write-Host "  Application Error 1000 records for Leaker since the crash: $($logged.Count)"

            $pw = Start-PoolWatch $Exe @('--headless', '--duration', '20', '--config', $cfgFile, '--out', $Out)
            $code = Wait-PoolWatch $pw 120
            Assert-True ($code -eq 0) "second run exit code 0 (got $code)"
            $all = Read-Findings
            foreach ($f in @($all | Where-Object { $_.kind -in 'VersionChange', 'Crash' })) { Write-Host "  finding: $($f.kind) $($f.title)" }
            Assert-True (@($all | Where-Object { $_.kind -eq 'VersionChange' -and $_.title -eq 'Version change: Claude Code updated 2.0.14 -> 2.0.15' }).Count -eq 1) "the update between runs is on the timeline"
            $summary = Get-Content "$Out\summary.json" -Raw | ConvertFrom-Json
            Assert-True ($summary.versions.'Claude Code' -eq '2.0.15') "summary has the current Claude Code version ($($summary.versions | ConvertTo-Json -Compress))"
            $crash = @($all | Where-Object { $_.kind -eq 'Crash' -and $_.title -like 'App crash: Leaker*' })
            if ($logged.Count -gt 0) {
                Assert-True ($crash.Count -ge 1) "the Leaker crash since the last run is reported ($($crash.Count))"
            } else {
                Write-Host "  ::warning::the runner logged no Application Error 1000 for Leaker (WER off?); the crash path is unit-tested only here"
            }
            # A third run must not report the same crash again (it reads only records after the second run's query).
            $pw = Start-PoolWatch $Exe @('--headless', '--duration', '12', '--config', $cfgFile, '--out', $Out)
            $code = Wait-PoolWatch $pw 120
            Assert-True ($code -eq 0) "third run exit code 0 (got $code)"
            $again = @(Read-Findings | Where-Object { $_.kind -eq 'Crash' -and $_.title -like 'App crash: Leaker*' })
            Assert-True ($again.Count -eq $crash.Count) "a crash is reported once, not again by a later run ($($crash.Count) then $($again.Count))"
        }
        finally {
            Remove-Item -Recurse -Force "$versions\2.0.14", "$versions\2.0.15" -ErrorAction SilentlyContinue
            if ($created) { Remove-Item -Recurse -Force (Join-Path $env:USERPROFILE '.local\share\claude') -ErrorAction SilentlyContinue }
        }
    }
    'tokenguard' {
        # Addendum 3: the token-leak guard's logon check with the real hidden helper. A runner may or may not have the
        # leak, so this asserts the code path and prints the numbers, not the verdict.
        Add-Type -Namespace PW -Name Spi -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfoW(uint a, uint u, ref uint v, uint w);
[DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfoW(uint a, uint u, System.IntPtr v, uint w);
'@
        function Get-FgLock { $v = [uint32]0; if ([PW.Spi]::SystemParametersInfoW(0x2000, 0, [ref]$v, 0)) { $v } else { $null } }
        $original = Get-FgLock
        Write-Host "  live ForegroundLockTimeout at start: $original"
        $cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
        $build = "$($cv.CurrentBuild).$($cv.UBR)"

        $pw = Start-PoolWatch $Exe @('--headless', '--duration', '150', '--config', "$PSScriptRoot\diag-config.json", '--out', $Out, '--token-guard')
        $code = Wait-PoolWatch $pw 400
        Assert-True ($code -eq 0) "headless exit code 0 (got $code)"
        $g = (Get-Content "$Out\summary.json" -Raw | ConvertFrom-Json).tokenGuard
        Write-Host "  guard: $($g | ConvertTo-Json -Compress)"
        Assert-True ($null -ne $g -and $g.checking -eq $false) "the logon check finished within the run"
        Assert-True ($g.step -in 'Test', 'AlreadyOn', 'TurnOnUnreported') "a first run without state tests (or finds the timeout already 0): $($g.step)"
        if ($g.step -eq 'Test') {
            Assert-True ((@($g.numbers).Count -eq 6) -or ($null -ne $g.error)) "the helper ran twice x three ways ($(@($g.numbers) -join ', '); error $($g.error))"
            Write-Host "  helper numbers (n = 100 per way): $(@($g.numbers) -join ', ')"
            Assert-True ($g.result -in 'leak', 'clean-once', 'unclear') "a verdict was recorded: $($g.result)"
            Assert-True ($g.build -eq $build) "the state carries the build ($($g.build) = $build)"
            if ($g.result -ne 'fixed') { Write-Host "  workaround wanted: $($g.wanted); live now: $($g.liveTimeout)" }
        }
        Assert-True (Test-Path "$Out\cache\token-guard.json") "the guard's state is saved"
        $tf = @(Read-Findings | Where-Object { $_.kind -eq 'TokenGuard' })
        Assert-True ($tf.Count -ge 1) "a TokenGuard finding on the timeline: $($tf[0].title)"
        Assert-True (@(Read-Findings | Where-Object { $_.kind -eq 'SpawnStorm' -and $_.title -like '*PoolWatch.exe*' }).Count -eq 0) "the guard's own test is not reported as a spawn storm"

        # A known leak: PoolWatch's own state from an earlier logon says this build leaks, so the next logon need not test.
        try {
            $out2 = "$Out-known"
            New-Item -ItemType Directory -Force "$out2\cache" | Out-Null
            @{ build = $build; result = 'leak'; tested = '2026-10-20T08:15:00Z'; detail = 'run 1 {1, 0, 2}, run 2 {100, 98, 101}' } | ConvertTo-Json | Set-Content "$out2\cache\token-guard.json"
            [void][PW.Spi]::SystemParametersInfoW(0x2001, 0, [System.IntPtr]200000, 2)
            Write-Host "  live timeout before the known-leak run: $(Get-FgLock)"
            $pw = Start-PoolWatch $Exe @('--headless', '--duration', '40', '--config', "$PSScriptRoot\diag-config.json", '--out', $out2, '--token-guard')
            $code = Wait-PoolWatch $pw 200
            Assert-True ($code -eq 0) "known-leak run: exit code 0 (got $code)"
            $g2 = (Get-Content "$out2\summary.json" -Raw | ConvertFrom-Json).tokenGuard
            Write-Host "  known-leak run: $($g2 | ConvertTo-Json -Compress)"
            Assert-True ($g2.step -in 'TurnOnKnownLeak', 'AlreadyOn') "same build + leak: no new test ($($g2.step))"
            Assert-True (@($g2.numbers).Count -eq 0) "no helper run"

            # M3: a leak test that never finished (PoolWatch killed during it) leaves its marker; the next start turns the
            # workaround on without testing again, and removes the marker once the outcome is saved.
            $out3 = "$Out-interrupted"
            New-Item -ItemType Directory -Force "$out3\cache" | Out-Null
            Set-Content "$out3\cache\token-test-running.json" '{}'
            [void][PW.Spi]::SystemParametersInfoW(0x2001, 0, [System.IntPtr]200000, 2)
            $pw = Start-PoolWatch $Exe @('--headless', '--duration', '40', '--config', "$PSScriptRoot\diag-config.json", '--out', $out3, '--token-guard')
            $code = Wait-PoolWatch $pw 200
            Assert-True ($code -eq 0) "interrupted run: exit code 0 (got $code)"
            $g3 = (Get-Content "$out3\summary.json" -Raw | ConvertFrom-Json).tokenGuard
            Write-Host "  interrupted run: $($g3 | ConvertTo-Json -Compress)"
            Assert-True ($g3.step -in 'TurnOnInterrupted', 'AlreadyOn') "an unfinished test's marker turns the workaround on without testing ($($g3.step))"
            Assert-True (@($g3.numbers).Count -eq 0) "no helper run after an interrupted test"
            Assert-True (-not (Test-Path "$out3\cache\token-test-running.json")) "the marker is removed once the outcome is saved"
        }
        finally {
            if ($null -ne $original) { [void][PW.Spi]::SystemParametersInfoW(0x2001, 0, [System.IntPtr]$original, 2) }
            Write-Host "  live timeout restored: $(Get-FgLock)"
        }
    }
    'services' {
        # Addendum 4: the background-services page. The runner is elevated. Asserts the code path, the gate, and a real
        # change + restore of a harmless catalogue service if one is installed (else just the gate and the dump).
        $dump = "$Out\services.json"
        $pw = Start-Process -FilePath $Exe -ArgumentList @('--services-dump', $dump, '--out', $Out, $script:AnyLocation) -Wait -PassThru -NoNewWindow
        Assert-True ($pw.ExitCode -eq 0 -and (Test-Path $dump)) "--services-dump wrote the services ($($pw.ExitCode))"
        $svc = Get-Content $dump -Raw | ConvertFrom-Json
        Write-Host "  services: $(@($svc.services).Count) ($(@($svc.services | Where-Object { $_.group -eq 'optional' }).Count) optional, $(@($svc.services | Where-Object { $_.group -eq 'protected' }).Count) protected)"
        Assert-True (@($svc.services).Count -gt 20) "enumerated the installed services"
        # The protected gate: security / update / input / core are protected and have a reason.
        foreach ($p in 'WinDefend', 'wuauserv', 'Audiosrv', 'RpcSs') {
            $row = @($svc.services | Where-Object { $_.name -eq $p })
            if ($row.Count -eq 1) { Assert-True ($row[0].group -eq 'protected' -and $row[0].protectedReason) "$p is protected ($($row[0].protectedReason))" }
        }
        # Turning a protected service off is refused.
        $r = Start-Process -FilePath $Exe -ArgumentList @('--service-off', 'WinDefend', '--out', $Out, $script:AnyLocation) -Wait -PassThru -NoNewWindow -RedirectStandardOutput "$Out\off-wd.txt"
        $wd = Get-Content "$Out\off-wd.txt" -Raw
        Write-Host "  off WinDefend -> $wd"
        Assert-True ($wd -match 'protected') "turning a protected service off is refused"
        Assert-True ((Get-Service WinDefend).StartType -ne 'Disabled') "WinDefend is unchanged"

        # A harmless catalogue service present on the runner (try a few): change + restore.
        $candidate = $null
        foreach ($name in 'MapsBroker', 'Fax', 'RetailDemo', 'WMPNetworkSvc', 'WalletService', 'PhoneSvc', 'icssvc') {
            $s = Get-Service $name -ErrorAction SilentlyContinue
            if ($s -and $s.StartType -ne 'Disabled') { $candidate = $name; break }
        }
        if ($candidate) {
            $before = (Get-CimInstance Win32_Service -Filter "Name='$candidate'").StartMode
            Write-Host "  candidate $candidate StartMode=$before"
            $r = Start-Process -FilePath $Exe -ArgumentList @('--service-off', $candidate, '--out', $Out, $script:AnyLocation) -Wait -PassThru -NoNewWindow -RedirectStandardOutput "$Out\off.txt"
            Write-Host "  off $candidate -> $(Get-Content "$Out\off.txt" -Raw)"
            Assert-True ((Get-Service $candidate).StartType -eq 'Disabled') "$candidate was turned off (disabled)"
            # The change log is always in the admin-only data root, never under --out (Part 3 review).
            $changes = Join-Path $env:ProgramData 'PoolWatch\service-changes.json'
            Assert-True (Test-Path $changes) "the original was saved before the change"
            $saved = Get-Content $changes -Raw | ConvertFrom-Json
            Assert-True (@($saved | Where-Object { $_.service -eq $candidate }).Count -eq 1) "service-changes.json records $candidate"
            $r = Start-Process -FilePath $Exe -ArgumentList @('--service-restore', $candidate, '--out', $Out, $script:AnyLocation) -Wait -PassThru -NoNewWindow -RedirectStandardOutput "$Out\restore.txt"
            Write-Host "  restore $candidate -> $(Get-Content "$Out\restore.txt" -Raw)"
            $after = (Get-CimInstance Win32_Service -Filter "Name='$candidate'").StartMode
            Assert-True ($after -eq $before) "restore put $candidate back to $before (got $after)"
        } else {
            Write-Host "  (no harmless catalogue service installed to change; gate and dump covered)"
        }

        # Tasks / startup dump (tab C) and the live load series.
        $td = "$Out\tasks.json"
        $pw = Start-Process -FilePath $Exe -ArgumentList @('--tasks-dump', $td, '--out', $Out, $script:AnyLocation) -Wait -PassThru -NoNewWindow
        Assert-True ($pw.ExitCode -eq 0 -and (Test-Path $td)) "--tasks-dump wrote the tasks and startup entries"
        $pw = Start-PoolWatch $Exe @('--headless', '--duration', '30', '--config', "$PSScriptRoot\diag-config.json", '--out', $Out)
        $code = Wait-PoolWatch $pw 120
        Assert-True ($code -eq 0) "headless exit code 0 (got $code)"
        $load = (Get-Content "$Out\summary.json" -Raw | ConvertFrom-Json).load
        Write-Host "  load: cpu=$($load.cpuFraction) runQueue=$($load.runQueue) topPf=$(@($load.top)[0].pageFaultsPerSec)"
        Assert-True ($null -ne $load -and $null -ne $load.cpuFraction) "the CPU / load series is recorded"
    }
}

Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
Complete-Assertions "Diagnostics e2e ($Scenario)"
