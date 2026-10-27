# Milestone 1 acceptance: collectors + headless mode + CSV output produce sane numbers on a real Windows box.
param([Parameter(Mandatory)] [string]$Exe, [Parameter(Mandatory)] [string]$Out)
. "$PSScriptRoot\common.ps1"

New-Item -ItemType Directory -Force $Out | Out-Null
$pw = Start-PoolWatch $Exe @('--headless', '--duration', '90', '--interval', '5', '--out', $Out)

# Reference values from an independent source while PoolWatch runs.
Start-Sleep -Seconds 20
$counters = Get-Counter '\Memory\Pool Paged Bytes', '\Memory\Pool Nonpaged Bytes'
$refPaged = ($counters.CounterSamples | Where-Object Path -like '*pool paged bytes').CookedValue
$refNonPaged = ($counters.CounterSamples | Where-Object Path -like '*pool nonpaged bytes').CookedValue

# A child the ETW collector must see (start, command line, exit code). It runs 33 s: processes that live under 30 s
# are rolled up per parent and image in events.csv (overhead fix, 2026-10-26), longer ones are listed individually.
$child = Start-Process cmd.exe -ArgumentList '/c', 'ping -n 34 127.0.0.1 >nul & exit 42' -PassThru -WindowStyle Hidden
$child.WaitForExit()
$childPid = $child.Id

$code = Wait-PoolWatch $pw 180
Assert-True ($code -eq 0) "headless exit code 0 (got $code)"

$totals = Import-PwCsv "$Out\totals.csv"
Assert-True ($totals.Count -ge 10) "totals.csv has >= 10 samples ($($totals.Count))"
$mid = $totals[[int]($totals.Count / 2)]
$paged = [double]$mid.committed_paged_bytes; $nonPaged = [double]$mid.committed_nonpaged_bytes
Assert-True ([math]::Abs($paged - $refPaged) / $refPaged -lt 0.25) ("committed paged {0:N0} MB within 25% of PDH {1:N0} MB" -f ($paged / 1MB), ($refPaged / 1MB))
Assert-True ([math]::Abs($nonPaged - $refNonPaged) / $refNonPaged -lt 0.25) ("committed nonpaged {0:N0} MB within 25% of PDH {1:N0} MB" -f ($nonPaged / 1MB), ($refNonPaged / 1MB))
$tsP = [double]$mid.tagsum_paged_bytes; $tsN = [double]$mid.tagsum_nonpaged_bytes
Assert-True ($tsP -gt 0.2 * $paged -and $tsP -lt 1.5 * $paged) ("tag-sum paged {0:N0} MB plausible vs committed" -f ($tsP / 1MB))
Assert-True ($tsN -gt 0.2 * $nonPaged -and $tsN -lt 1.5 * $nonPaged) ("tag-sum nonpaged {0:N0} MB plausible vs committed" -f ($tsN / 1MB))
Assert-True ([int]$mid.tag_count -gt 100) "tag count > 100 ($($mid.tag_count))"
Assert-True ([int]$mid.process_count -gt 20) "process count > 20 ($($mid.process_count))"

$tags = Import-PwCsv "$Out\tags.csv"
$file = @($tags | Where-Object { $_.tag -ceq 'File' })
Assert-True ($file.Count -ge 1) "tags.csv tracks File"
Assert-True (@($tags | Where-Object { $_.tag -ceq 'Toke' }).Count -ge 1) "tags.csv tracks Toke (always tracked)"
# Each raw tag value must map to exactly one displayed name, and one sample must never list a tag twice
# (keys are the raw 4 bytes, so e.g. Reff and RefF stay separate rows).
$badHex = @($tags | Group-Object tag_hex | Where-Object { @($_.Group | ForEach-Object { $_.tag } | Sort-Object -CaseSensitive -Unique).Count -ne 1 })
Assert-True ($badHex.Count -eq 0) "each tag_hex maps to one tag name ($($badHex.Count) conflicts)"
$dupes = @($tags | Group-Object time, tag_hex | Where-Object Count -gt 1)
Assert-True ($dupes.Count -eq 0) "no duplicate tag rows within a sample ($($dupes.Count))"

$procs = Import-PwCsv "$Out\processes.csv"
Assert-True (@($procs | Where-Object { $_.image -eq 'PoolWatch.exe' }).Count -ge 1) "processes.csv includes PoolWatch.exe"
Assert-True (@($procs | Where-Object { $_.pid -eq '4' }).Count -ge 1) "processes.csv includes System (PID 4)"
$self = @($procs | Where-Object { $_.image -eq 'PoolWatch.exe' })[-1]
Assert-True ([int]$self.handles -gt 50 -and [int]$self.threads -gt 3) "own handles/threads plausible ($($self.handles) / $($self.threads))"

$events = Import-PwCsv "$Out\events.csv"
$start = @($events | Where-Object { $_.kind -eq 'Start' -and $_.pid -eq "$childPid" })
$exit = @($events | Where-Object { $_.kind -eq 'Exit' -and $_.pid -eq "$childPid" })
Assert-True ($start.Count -ge 1) "events.csv has Start for child cmd.exe ($childPid)"
Assert-True ($start.Count -ge 1 -and $start[0].command_line -like '*exit 42*') "child command line captured: $($start[0].command_line)"
Assert-True ($start.Count -ge 1 -and $start[0].user -ne '') "child user captured: $($start[0].user)"
Assert-True ($exit.Count -ge 1 -and $exit[0].exit_code -eq '42') "events.csv has Exit with code 42 (got '$($exit[0].exit_code)')"

$handles = Import-PwCsv "$Out\handles.csv"
Assert-True (@($handles | Where-Object { $_.pid -eq '-1' -and $_.type -eq 'File' }).Count -ge 1) "handles.csv has system-wide File handle totals"
Assert-True (@($handles | Where-Object { $_.image -eq 'PoolWatch.exe' }).Count -ge 1) "handles.csv has PoolWatch's own handles by type"

$services = Import-PwCsv "$Out\services.csv"
Assert-True ($services.Count -gt 20) "services.csv has > 20 running services ($($services.Count))"

$volumes = Import-PwCsv "$Out\volumes.csv"
$c = @($volumes | Where-Object { $_.root -like 'C:*' })
Assert-True ($c.Count -ge 1 -and $c[0].file_system -eq 'NTFS') "volumes.csv: C: is NTFS"
Assert-True ($c.Count -ge 1 -and $c[0].dev_drive -in @('True', 'False')) "volumes.csv: Dev Drive flag answered for C: ($($c[0].dev_drive))"

$summary = Get-Content "$Out\summary.json" -Raw | ConvertFrom-Json
Assert-True ($summary.etwRunning -eq $true -and [int]$summary.etwEvents -gt 0) "ETW process session ran (mode $($summary.etwMode), $($summary.etwEvents) events, $($summary.etwEventsLost) lost)"
Assert-True ([int]$summary.samples -ge 12) "at least 12 samples taken ($($summary.samples))"
Write-Host (Get-Content "$Out\overhead.json" -Raw)

Complete-Assertions 'M1 headless'
