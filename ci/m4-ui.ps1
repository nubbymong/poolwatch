# Milestone 4 acceptance: the tray app starts and every window under review renders, each drawn to a PNG (the
# ui-screenshots artifact) and checked: the main window's Simple view (status line, Clear cache now, the next-clear line,
# the graph, up to three suggestions) and Advanced view (three buttons, the Pool causes and Services tabs), the plain
# Settings form and its Advanced grid, the setup guide, Active changes, Report an issue, About and the update notice.
# Nothing in them is clipped or overlaps, no internal name shows, everything taken off the window is in the tray menu,
# every window made for the test is closed again, and the app exits cleanly. Runs a small File-handle leak meanwhile so
# Pool causes has content, and turns one harmless service off first so Active changes has a service to list.
param([Parameter(Mandatory)] [string]$Exe, [Parameter(Mandatory)] [string]$Leaker, [Parameter(Mandatory)] [string]$Out)
. "$PSScriptRoot\common.ps1"

New-Item -ItemType Directory -Force $Out | Out-Null
$work = Join-Path $env:RUNNER_TEMP 'leaker-ui'
New-Item -ItemType Directory -Force $work | Out-Null

# A harmless catalogue service on this runner (the candidates ci/diag-e2e.ps1 uses), turned off before the test and put
# back after it.
$service = foreach ($name in 'MapsBroker', 'Fax', 'RetailDemo', 'WMPNetworkSvc', 'WalletService', 'PhoneSvc', 'icssvc') {
    $s = Get-Service $name -ErrorAction SilentlyContinue
    if ($s -and $s.StartType -ne 'Disabled') { $name; break }
}
if ($service) {
    $r = Start-Process -FilePath $Exe -ArgumentList @('--service-off', $service, '--out', $Out, $script:AnyLocation) -Wait -PassThru -NoNewWindow -RedirectStandardOutput "$Out\service-off.txt"
    Assert-True ($r.ExitCode -eq 0) "turned $service off for Active changes (exit $($r.ExitCode))"
} else { Write-Host '  (no harmless catalogue service on this runner: Active changes lists the setting only)' }

# ui-config.json: the M2 thresholds plus the hourly automatic clear; the default 10 s interval.
$pw = Start-PoolWatch $Exe @('--ui-smoke', '80', '--config', "$PSScriptRoot\ui-config.json", '--interval', '10', '--out', $Out)
Start-Sleep -Seconds 15
& $Leaker leak-handles --dir $work --count 30000 --ramp 30 --hold 40
$code = Wait-PoolWatch $pw 300
Assert-True ($code -eq 0) "UI smoke exit code 0 (got $code)"
Get-Content "$Out\poolwatch.log" | Select-String 'ui smoke|incident|failed' | ForEach-Object { Write-Host "  log: $_" }
if ($service) {
    $r = Start-Process -FilePath $Exe -ArgumentList @('--service-restore', 'all', '--out', $Out, $script:AnyLocation) -Wait -PassThru -NoNewWindow -RedirectStandardOutput "$Out\service-restore.txt"
    Assert-True ($r.ExitCode -eq 0 -and (Get-Service $service).StartType -ne 'Disabled') "put $service back (exit $($r.ExitCode))"
}

# Setting names and run-together setting-like words never reach the owner (the plain-words rule). $internal adds every
# setting's own name, which the app lists in smoke.json, so a new setting is covered without a change here.
$internal = '\b[A-Z][a-z]+(?:[A-Z][a-z]+)+(Enabled|Hours|Gb|Minutes)\b'
function Assert-Plain([string]$where, $texts) {
    $bad = @(@($texts) | Where-Object { "$_" -cmatch $internal })
    Assert-True ($bad.Count -eq 0) "no internal names in $where$(if ($bad.Count) { ': ' + ($bad -join ' | ') })"
}

$smokeFile = "$Out\screenshots\smoke.json"
Assert-True (Test-Path $smokeFile) "smoke.json written"
if (Test-Path $smokeFile) {
    $s = Get-Content $smokeFile -Raw | ConvertFrom-Json
    Write-Host "  smoke: $($s | ConvertTo-Json -Compress -Depth 8)"
    $settingNames = @($s.settingNames)
    Assert-True ($settingNames.Count -gt 0) "the app listed its setting names ($($settingNames.Count))"
    $internal = (@($internal) + @($settingNames | ForEach-Object { '\b' + [regex]::Escape($_) + '\b' })) -join '|'
    $f = $s.form
    $shots = @($s.screenshots)
    $names = '1-simple-view.png', '2-advanced-view.png', '3-services.png', '4-settings.png', '5-settings-advanced.png', '6-setup-guide.png',
             '7-active-changes.png', '8-report-issue.png', '9-about.png', '10-update-notice.png'
    Assert-True ($shots.Count -eq $names.Count) "$($names.Count) screenshots ($($shots -join ', '))"
    foreach ($n in $names) { Assert-True ($n -in $shots) "screenshot $n taken" }

    # The status line, the same in both views.
    Assert-True ($f.status -match '^Kernel pool') "the status line reads in plain English: $($f.status)"
    # The words say which way the pool went, so no amount carries a sign ("+708.5 MB", "-1 GB"). Only amounts are checked:
    # a tag or process name may hold a "-8" or a "+" of its own ("UTF-8", "MSb+").
    Assert-True ($f.status -notmatch '[+-]\d[\d.]* [KMG]B|Rising') "the status line never prints a signed amount: $($f.status)"
    # A likely cause is named as people know it, never by its exe or PID ("Leaker", not "Leaker.exe (PID 7716)"). Only the
    # cause sentence's shape is checked: a tag's description may name a driver file of its own.
    Assert-True ($f.status -notmatch '\(PID \d+\)|keeping an eye on \S+\.exe') "the status line names a cause as people know it: $($f.status)"
    Assert-True ([int]$f.chartPoints -ge 2) "the graph has data ($($f.chartPoints) points)"

    # The Simple view, as the window opens: one button, the footer, no tabs, the next-clear line, at most three cards.
    $simple = $f.simple
    Assert-True ((@($simple.buttons) -join '|') -eq 'Clear cache now') "the Simple view has one button: $(@($simple.buttons) -join ', ')"
    Assert-True ((@($simple.footer) -join '|') -eq 'Active changes|Advanced view') "the footer has Active changes and Advanced view: $(@($simple.footer) -join ', ')"
    Assert-True ($simple.tabsShown -eq $false) "the Simple view shows no tabs"
    Assert-True ($simple.view.nextClear -match '^Next cache clear in (under a minute|\d+ (min|h))') "the next-clear line shows the schedule: $($simple.view.nextClear)"
    $cards = @($simple.view.cards)
    Assert-True ($cards.Count -le 3) "at most three suggestions ($($cards.Count): $(@($cards | ForEach-Object { $_.title }) -join ' | '))"
    foreach ($c in $cards) { Assert-True (@($c.buttons).Count -eq 1) "the suggestion '$($c.title)' has one button ($(@($c.buttons) -join ', '))" }
    $simpleProblems = @($simple.layoutProblems) + @($simple.view.layoutProblems)
    Assert-True ($simpleProblems.Count -eq 0) "the Simple view: nothing clipped or overlapping: $($simpleProblems -join '; ')"
    Assert-Plain 'the Simple view' (@($f.status, $simple.view.nextClear) + @($cards | ForEach-Object { $_.title, $_.why }) + @($cards | ForEach-Object { $_.buttons }))

    # The Advanced view: three buttons, two tabs.
    Assert-True ((@($f.buttons) -join '|') -eq 'Clear cache now|Export report|Settings') "the Advanced view has three buttons: $(@($f.buttons) -join ', ')"
    Assert-True ((@($f.tabs) -join '|') -eq 'Pool causes|Services') "two tabs: $(@($f.tabs) -join ', ')"

    # Pool causes: the leak's incident is listed, and selecting a row shows its details.
    Assert-True ([int]$f.rows.'Pool causes' -ge 1) "Pool causes lists rows ($($f.rows.'Pool causes'); by kind: $($f.causeKinds | ConvertTo-Json -Compress))"
    Assert-True ([int]$f.causeKinds.Incident -ge 1) "Pool causes lists the leak's incident ($([int]$f.causeKinds.Incident))"
    Assert-True ("$($f.detailsTitle)".Length -gt 0 -and "$($f.details)".Length -gt 0) "selecting the top row shows its details: $($f.detailsTitle)"
    # Every cause cell is a cause, a blank (not ranked yet) or "no clear cause yet": never an instruction to the user.
    Assert-True (@(@($f.likelyCauses) | Where-Object { $_ -match 'select the row|to rank' }).Count -eq 0) "no placeholder in Likely cause: $(@($f.likelyCauses) -join ' | ')"
    Write-Host "  tag rows still without a ranking at the end: $($f.unrankedTagRows)"
    Assert-True ([double]$f.identityKept -ge 0.8) "a refresh updates rows in place: $([math]::Round([double]$f.identityKept * 100))% of row objects kept (no clear-and-rebuild)"
    Assert-True ($f.selectionKept -eq $true) "a refresh keeps the selected row"
    Assert-True ($f.copyMatches -eq $true) "Ctrl+C copies the header and exactly the selected row's cells: $($f.copiedSample)"

    # Nothing clipped or overlapping (text that does not fit, controls outside their parent, the chart's labels on its
    # title), checked with each tab selected (Services once its list has loaded) and with Settings open.
    Assert-True (@($f.layoutProblems).Count -eq 0) "Pool causes and Services: nothing clipped or overlapping at $($f.size.width) x $($f.size.height) ($($f.size.dpi) dpi): $(@($f.layoutProblems) -join '; ')"
    Assert-True ([int]$f.rows.Services -ge 1) "the Services tab was checked with its list loaded ($($f.rows.Services) services)"
    Assert-True (@($f.settingsLayoutProblems).Count -eq 0) "Settings and Settings Advanced: nothing clipped: $(@($f.settingsLayoutProblems) -join '; ')"
    # The plain Settings form: its five parts in plain words, the lines under the token-leak guard and update notices, and
    # Advanced for the full grid. (The layout check above covers the whole form at its own size: it sizes itself.)
    $form = @($f.settingsForm)
    foreach ($item in 'Start at logon', 'Start PoolWatch when I sign in', 'Automatic cache clearing', 'Clear the standby cache automatically',
                      'Deeper capture', 'Deeper automatic captures', 'Token-leak guard', 'Guard against a kernel pool leak in Windows 11 24H2 and 25H2',
                      'Update notices', 'Tell me when a new version is out.', 'Checks GitHub once a day; information only, nothing is downloaded or installed.',
                      'Advanced...', 'Save', 'Close') {
        Assert-True ($item -in $form) "Settings shows '$item'"
    }
    Assert-True (@($form | Where-Object { $_ -like 'While it is on, other programs can bring their windows to the front*until you untick this.' }).Count -eq 1) "Settings says what the token-leak guard does to you and how long it lasts"
    Assert-True (@($form | Where-Object { $_ -like '*Save puts automatic clearing, the token-leak guard and update notices into effect at once*' }).Count -eq 1) "Settings says what Save applies at once"
    Assert-Plain 'the Settings form' $form
    if (@($f.layoutProblemsAtMinimumSize).Count -gt 0) { Write-Host "  note, at the smallest window size: $(@($f.layoutProblemsAtMinimumSize) -join '; ')" }
    Assert-True ($f.timerWhileVisible -eq $true -and $f.timerWhileHidden -eq $false) "refresh timer runs only while visible (visible=$($f.timerWhileVisible), hidden=$($f.timerWhileHidden))"
    Assert-True ([int]$f.rowsAfterHide -ge 1) "hidden and shown again, Pool causes lists its rows again ($($f.rowsAfterHide))"

    # The setup guide, after it read the PC (no leak check in the test): the first-run rows and Done, nothing clipped.
    $g = $s.guide
    Assert-True ($g.ready -eq $true) "the setup guide read the PC"
    $titles = @($g.stats.rows | ForEach-Object { $_.title })
    foreach ($row in 'Start PoolWatch when you sign in', 'Clear the cache every hour', 'Protect this PC from the Windows token leak') {
        Assert-True ($row -in $titles) "the setup guide lists '$row' ($($titles -join ' | '))"
    }
    Assert-True ($null -ne $g.stats.updates) "the setup guide has the update notices switch"
    Assert-True ($g.stats.done -eq 'Done') "the setup guide closes with Done"
    Assert-True (@($g.stats.layoutProblems).Count -eq 0) "the setup guide: nothing clipped: $(@($g.stats.layoutProblems) -join '; ')"
    Assert-Plain 'the setup guide' (@($g.stats.facts) + @($g.stats.rows | ForEach-Object { $_.title, $_.why, $_.button }))

    # Active changes: each row with its own button (Turn off, Remove or Put back), Put all back and Close (no restore
    # point), the "Not listed" line and the uninstall note.
    $a = $s.activeChanges
    $rows = @($a.stats.rows)
    Assert-True ($a.loaded -eq $true) "Active changes read its list"
    Assert-True ("$($a.stats.title)" -eq 'Active changes') "the window is called Active changes ($($a.stats.title))"
    Assert-True ($rows.Count -ge $(if ($service) { 2 } else { 1 })) "Active changes lists the changes ($($rows.Count): $(@($rows | ForEach-Object { $_.what }) -join ' | '))"
    Assert-True ([int]$a.stats.undoButtons -eq $rows.Count) "a Turn off, Remove or Put back button per row ($($a.stats.undoButtons))"
    Assert-True ((@($a.stats.buttons) -join '|') -eq 'Put all back|Close') "Active changes has Put all back and Close: $(@($a.stats.buttons) -join ', ')"
    if ($service) { Assert-True (@($rows | Where-Object { $_.group -eq 'TurnedOff' -and $_.button -eq 'Put back' }).Count -ge 1) "the service PoolWatch turned off has Put back" }
    Assert-True ("$($a.stats.notListed)" -like 'Not listed:*') "Active changes says what it does not list"
    Assert-True ("$($a.stats.uninstallNote)" -like 'Uninstalling puts all of these back*') "Active changes says uninstalling puts them back"
    Assert-True (@($a.stats.layoutProblems).Count -eq 0) "Active changes: nothing clipped: $(@($a.stats.layoutProblems) -join '; ')"
    Assert-Plain 'Active changes' (@($rows | ForEach-Object { $_.what, $_.title, $_.detail, $_.note }))

    # Report an issue: the preview is redacted, and the address carries only the title, version and Windows.
    $rep = $s.report
    Assert-True ("$($rep.preview)".IndexOf($env:USERNAME, [StringComparison]::OrdinalIgnoreCase) -lt 0) "the report's preview does not hold the user name"
    Assert-True ("$($rep.preview)".IndexOf($env:COMPUTERNAME, [StringComparison]::OrdinalIgnoreCase) -lt 0) "the report's preview does not hold the PC's name"
    Assert-True ([int]$rep.urlLength -gt 0 -and [int]$rep.urlLength -le 255) "the issue address is short ($($rep.urlLength) characters)"
    Assert-True (@($rep.layoutProblems).Count -eq 0) "Report an issue: nothing clipped: $(@($rep.layoutProblems) -join '; ')"

    # About: the credits, and nothing clipped.
    $credits = @($s.about.credits)
    foreach ($name in 'TraceEvent', 'Inno Setup', 'gunrgbcr', 'bentoner', 'RAMMap') {
        Assert-True (@($credits | Where-Object { $_ -like "*$name*" }).Count -ge 1) "About credits $name"
    }
    Assert-True (@($s.about.layoutProblems).Count -eq 0) "About: nothing clipped: $(@($s.about.layoutProblems) -join '; ')"

    # The update notice for a sample release, at the bottom of the main window.
    Assert-True ($s.updateNotice.text -eq "PoolWatch 1.0.1 is available. See what's new") "the update notice reads: $($s.updateNotice.text)"
    Assert-True (@($s.updateNotice.layoutProblems).Count -eq 0) "the update notice: nothing clipped: $(@($s.updateNotice.layoutProblems) -join '; ')"

    # Everything taken off the window is in the tray menu (the clear-task removals show only when there is one to remove).
    $tray = @($s.trayMenu)
    foreach ($item in 'Open PoolWatch', 'Set up PoolWatch...', 'Active changes...', 'Capture now', 'Since boot report', 'Open incident folder',
                      'Export vendor bundle...', 'Copy latest incident summary', 'Remove my clear task...', 'Remove old clear-cache tasks...',
                      'Report an issue...', 'About PoolWatch', 'Exit') {
        Assert-True ($item -in $tray) "tray menu has '$item'"
    }
    # Every window made for the test was closed again.
    Assert-True ((@($s.openForms) -join '|') -eq 'PoolWatch') "only the main window is left open ($(@($s.openForms) -join ', '))"

    # The tray colour follows what decides it, read together: amber or red while an incident is open or a tag holds memory.
    $watched = [int]$s.openIncidents -gt 0 -or [int]$s.retainedTags -gt 0
    Assert-True ($(if ($watched) { $s.trayIcon -in @('Red', 'Amber') } else { $s.trayIcon -eq 'Green' })) "tray icon $($s.trayIcon) with $($s.openIncidents) open incident(s) and $($s.retainedTags) tag(s) holding memory"
    foreach ($n in $shots) {
        $p = Join-Path "$Out\screenshots" $n
        Assert-True ((Test-Path $p) -and (Get-Item $p).Length -gt 10KB) "$n is a real image ($((Get-Item $p -ErrorAction SilentlyContinue).Length) bytes)"
    }
}
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
Complete-Assertions 'M4 UI'
