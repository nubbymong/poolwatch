# Overhead check (DESIGN.md section 3): run headless idle for 10 minutes at the default 10 s interval.
param([Parameter(Mandatory)] [string]$Exe, [Parameter(Mandatory)] [string]$Out, [int]$Seconds = 600)
. "$PSScriptRoot\common.ps1"

New-Item -ItemType Directory -Force $Out | Out-Null
$pw = Start-PoolWatch $Exe @('--headless', '--duration', "$Seconds", '--out', $Out)
$code = Wait-PoolWatch $pw ($Seconds + 120)
Assert-True ($code -eq 0) "headless exit code 0 (got $code)"
$o = Get-Content "$Out\overhead.json" -Raw | ConvertFrom-Json
$summary = Get-Content "$Out\summary.json" -Raw | ConvertFrom-Json
$expected = [int]($Seconds / 10) - 5
# Guard against a vacuous pass: the budget only means something if the collectors actually ran.
Assert-True ([int]$summary.samples -ge $expected) "took >= $expected samples ($($summary.samples))"
Assert-True ([double]$o.steadyStateSeconds -ge ($Seconds - 100)) "steady-state window >= $($Seconds - 100) s ($($o.steadyStateSeconds))"
Assert-True (@(Import-PwCsv "$Out\totals.csv").Count -ge $expected) "totals.csv has >= $expected rows"
Assert-True (-not (Select-String -Path "$Out\poolwatch.log" -Pattern 'sample failed' -Quiet)) "no failed samples in the log"
$scanTimes = @(Import-PwCsv "$Out\handles.csv" | Select-Object -ExpandProperty time -Unique).Count
# A scan over the 250 ms budget skips the next one (DESIGN.md section 4), so skipped cycles are not failures.
$skipped = @(Select-String -Path "$Out\poolwatch.log" -Pattern 'skipping the next cycle').Count
$expectedScans = [int]($Seconds / 300) - $skipped  # every 5 minutes (plus the one at start)
Assert-True ($scanTimes -ge $expectedScans) "handle scans ran: $scanTimes distinct scan times (>= $expectedScans)"
Assert-True (-not (Select-String -Path "$Out\poolwatch.log" -Pattern 'handle scan failed' -Quiet)) "no failed handle scans in the log"
Assert-True ($summary.etwRunning -eq $true) "ETW process session still running at the end"
Write-Host (Get-Content "$Out\overhead.json" -Raw)
Assert-True ($o.cpuPercentOfOneCore -lt 0.5) ("CPU {0:N3}% of one core < 0.5%" -f $o.cpuPercentOfOneCore)
Assert-True ($o.peakWorkingSetBytes -lt 100MB) ("peak working set {0:N1} MB < 100 MB" -f ($o.peakWorkingSetBytes / 1MB))
Assert-True ($o.diskWriteBytesPerHour -lt 5MB) ("disk writes {0:N2} MB/h < 5 MB/h" -f ($o.diskWriteBytesPerHour / 1MB))
Complete-Assertions 'Overhead'
