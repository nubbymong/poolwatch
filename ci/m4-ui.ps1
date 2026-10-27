# Milestone 4 acceptance: the tray app starts, the main window renders (one status line, one graph, three buttons, the
# Pool causes and Services tabs, and the Settings dialog), nothing in it is clipped or overlaps, everything taken off the
# window is in the tray menu, and it exits cleanly. Runs a small File-handle leak meanwhile so Pool causes has content.
# The screenshots (main window, Pool causes with a row selected, Services, Settings) are the README's.
param([Parameter(Mandatory)] [string]$Exe, [Parameter(Mandatory)] [string]$Leaker, [Parameter(Mandatory)] [string]$Out)
. "$PSScriptRoot\common.ps1"

New-Item -ItemType Directory -Force $Out | Out-Null
$work = Join-Path $env:RUNNER_TEMP 'leaker-ui'
New-Item -ItemType Directory -Force $work | Out-Null
# The default 10 s interval.
$pw = Start-PoolWatch $Exe @('--ui-smoke', '80', '--config', "$PSScriptRoot\m2-config.json", '--interval', '10', '--out', $Out)
Start-Sleep -Seconds 15
& $Leaker leak-handles --dir $work --count 30000 --ramp 30 --hold 40
$code = Wait-PoolWatch $pw 240
Assert-True ($code -eq 0) "UI smoke exit code 0 (got $code)"
Get-Content "$Out\poolwatch.log" | Select-String 'ui smoke|incident|failed' | ForEach-Object { Write-Host "  log: $_" }

$smokeFile = "$Out\screenshots\smoke.json"
Assert-True (Test-Path $smokeFile) "smoke.json written"
if (Test-Path $smokeFile) {
    $s = Get-Content $smokeFile -Raw | ConvertFrom-Json
    Write-Host "  smoke: $($s | ConvertTo-Json -Compress -Depth 6)"
    $f = $s.form
    $shots = @($s.screenshots)
    Assert-True ($shots.Count -eq 4) "4 screenshots ($($shots -join ', '))"
    foreach ($n in '1-main-window.png', '2-pool-causes.png', '3-services.png', '4-settings.png') { Assert-True ($n -in $shots) "screenshot $n taken" }

    # The window: a plain status line, one graph, three buttons, two tabs.
    Assert-True ($f.status -match '^Kernel pool') "the status line reads in plain English: $($f.status)"
    # (Only the amounts: the text before the cause, whose name may hold a "-2" of its own.)
    Assert-True ((($f.status -split 'Most likely cause')[0]) -notmatch '\+-|-\d|Rising') "the status line never prints a negative rise: $($f.status)"
    Assert-True ((@($f.buttons) -join '|') -eq 'Clear cache now|Export report|Settings') "three buttons: $(@($f.buttons) -join ', ')"
    Assert-True ((@($f.tabs) -join '|') -eq 'Pool causes|Services') "two tabs: $(@($f.tabs) -join ', ')"
    Assert-True ([int]$f.chartPoints -ge 2) "the graph has data ($($f.chartPoints) points)"

    # Pool causes: the leak's incident is listed, and selecting a row shows its details.
    Assert-True ([int]$f.rows.'Pool causes' -ge 1) "Pool causes lists rows ($($f.rows.'Pool causes'); by kind: $($f.causeKinds | ConvertTo-Json -Compress))"
    Assert-True ([int]$f.causeKinds.Incident -ge 1) "Pool causes lists the leak's incident ($([int]$f.causeKinds.Incident))"
    Assert-True ("$($f.detailsTitle)".Length -gt 0 -and "$($f.details)".Length -gt 0) "selecting the top row shows its details: $($f.detailsTitle)"
    Assert-True ([double]$f.identityKept -ge 0.8) "a refresh updates rows in place: $([math]::Round([double]$f.identityKept * 100))% of row objects kept (no clear-and-rebuild)"
    Assert-True ($f.selectionKept -eq $true) "a refresh keeps the selected row"
    Assert-True ($f.copyMatches -eq $true) "Ctrl+C copies the header and exactly the selected row's cells: $($f.copiedSample)"

    # Nothing clipped or overlapping (text that does not fit, controls outside their parent, the chart's labels on its title).
    Assert-True (@($f.layoutProblems).Count -eq 0) "nothing clipped or overlapping at $($f.size.width) x $($f.size.height) ($($f.size.dpi) dpi): $(@($f.layoutProblems) -join '; ')"
    Assert-True (@($f.settingsLayoutProblems).Count -eq 0) "Settings: nothing clipped: $(@($f.settingsLayoutProblems) -join '; ')"
    if (@($f.layoutProblemsAtMinimumSize).Count -gt 0) { Write-Host "  note, at the smallest window size: $(@($f.layoutProblemsAtMinimumSize) -join '; ')" }

    # Everything taken off the window is in the tray menu (the clear-task removals show only when there is one to remove).
    $tray = @($s.trayMenu)
    foreach ($item in 'Open PoolWatch', 'Capture now', 'Since boot report', 'Open incident folder', 'Export vendor bundle...', 'Copy latest incident summary',
                      'Remove my clear task...', 'Remove old clear-cache tasks...', 'About PoolWatch', 'Exit') {
        Assert-True ($item -in $tray) "tray menu has '$item'"
    }

    Assert-True ($f.timerWhileVisible -eq $true -and $f.timerWhileHidden -eq $false) "refresh timer runs only while visible (visible=$($f.timerWhileVisible), hidden=$($f.timerWhileHidden))"
    Assert-True ($s.trayIcon -in @('Red', 'Amber')) "tray icon reflects the leak ($($s.trayIcon))"
    foreach ($n in $shots) {
        $p = Join-Path "$Out\screenshots" $n
        Assert-True ((Test-Path $p) -and (Get-Item $p).Length -gt 10KB) "$n is a real image ($((Get-Item $p -ErrorAction SilentlyContinue).Length) bytes)"
    }
}
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
Complete-Assertions 'M4 UI'
