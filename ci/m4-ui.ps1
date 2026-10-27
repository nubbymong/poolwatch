# Milestone 4 acceptance: the tray app starts, the main window renders every tab (screenshots for the README), and
# it exits cleanly. Runs a small File-handle leak meanwhile so the tag / suspect views have content.
param([Parameter(Mandatory)] [string]$Exe, [Parameter(Mandatory)] [string]$Leaker, [Parameter(Mandatory)] [string]$Out)
. "$PSScriptRoot\common.ps1"

New-Item -ItemType Directory -Force $Out | Out-Null
$work = Join-Path $env:RUNNER_TEMP 'leaker-ui'
New-Item -ItemType Directory -Force $work | Out-Null
# The default 10 s interval (the 2 s refresh must still show real per-second I/O between samples).
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
    Write-Host "  smoke: $($s | ConvertTo-Json -Compress)"
    Assert-True (@($s.screenshots).Count -eq 7) "7 tab screenshots ($(@($s.screenshots).Count))"
    Assert-True ($s.form.overview -match 'Kernel pool now:') "Overview page shows the pool now ($($s.form.overview -split "`n" | Select-Object -First 1))"
    Assert-True ($s.form.answer -match 'grew|fell|flat') "the selected tag has a plain answer line: $($s.form.answer)"
    Assert-True ([int]$s.form.rows.'Growing tags' -ge 1) "Growing tags list shows rows ($($s.form.rows.'Growing tags'))"
    Assert-True ([int]$s.form.rows.Processes -gt 20) "Processes list shows rows ($($s.form.rows.Processes))"
    Assert-True ([int]$s.form.processRowsWithIo -gt 0) "Processes list shows non-zero I/O rates ($($s.form.processRowsWithIo) rows)"
    Assert-True ([int]$s.form.rows.Incidents -ge 1) "Incidents list shows the leak's incident ($($s.form.rows.Incidents))"
    Assert-True ([int]$s.form.suspectRows -ge 1) "suspects shown for the selected tag ($($s.form.suspectRows))"
    Assert-True ([double]$s.form.identityKept -ge 0.8) "a refresh updates rows in place: $([math]::Round([double]$s.form.identityKept * 100))% of row objects kept (no clear-and-rebuild)"
    Assert-True ($s.form.selectionKept -eq $true) "a refresh keeps the selected row"
    Assert-True ($s.form.copyMatches -eq $true) "Ctrl+C copies the header and exactly the selected row's cells: $($s.form.copiedSample)"
    Assert-True ($s.form.timerWhileVisible -eq $true -and $s.form.timerWhileHidden -eq $false) "refresh timer runs only while visible (visible=$($s.form.timerWhileVisible), hidden=$($s.form.timerWhileHidden))"
    Assert-True ($s.trayIcon -in @('Red', 'Amber')) "tray icon reflects the leak ($($s.trayIcon))"
    foreach ($f in $s.screenshots) {
        $p = Join-Path "$Out\screenshots" $f
        Assert-True ((Test-Path $p) -and (Get-Item $p).Length -gt 10KB) "$f is a real image ($((Get-Item $p -ErrorAction SilentlyContinue).Length) bytes)"
    }
}
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
Complete-Assertions 'M4 UI'
