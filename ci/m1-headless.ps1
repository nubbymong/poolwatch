# Milestone 1 acceptance: collectors + headless mode + CSV output produce sane numbers on a real Windows box.
param([Parameter(Mandatory)] [string]$Exe, [Parameter(Mandatory)] [string]$Out)
. "$PSScriptRoot\common.ps1"

New-Item -ItemType Directory -Force $Out | Out-Null
$pw = Start-PoolWatch $Exe @('--headless', '--duration', '90', '--interval', '5', '--out', $Out)

# Reference values from an independent source while PoolWatch runs: PDH (PoolWatch reads GetPerformanceInfo). One read
# can land on a paged-pool spike shorter than a sample interval (a runner once read 299 MB from PDH while PoolWatch's
# samples 2 s before and 3 s after it read 188 and 190 MB), so there are three reads, each paired below with the
# totals.csv row nearest to it in time. Timeline, in seconds after PoolWatch starts (it samples every 5 s from about
# 0.5 s and stops at about 91):
#   about 21-23  read 1 (the first Get-Counter of a session takes 1-3 s)
#   then 33 s    the child below
#   about 55-58  read 2, once the child has exited
#   about 66-69  read 3, about 11 s later: over 20 s before PoolWatch stops
# The reads are over 10 s apart, so each pairs with a different sample, and a spike shorter than a sample interval, in
# a read or in a sample, spoils at most one of the three pairs.
function Read-Pdh {
    $c = Get-Counter '\Memory\Pool Paged Bytes', '\Memory\Pool Nonpaged Bytes'
    # Timed as Get-Counter returns: it reports its last read, taken after its 1 s sample interval, just before.
    [pscustomobject]@{
        At = [DateTime]::UtcNow
        Paged = ($c.CounterSamples | Where-Object Path -like '*pool paged bytes').CookedValue
        NonPaged = ($c.CounterSamples | Where-Object Path -like '*pool nonpaged bytes').CookedValue
    }
}
Start-Sleep -Seconds 20
$reads = @(Read-Pdh)

# A child the ETW collector must see (start, command line, exit code). It runs 33 s: processes that live under 30 s
# are rolled up per parent and image in events.csv (overhead fix, 2026-10-26), longer ones are listed individually.
$child = Start-Process cmd.exe -ArgumentList '/c', 'ping -n 34 127.0.0.1 >nul & exit 42' -PassThru -WindowStyle Hidden
$child.WaitForExit()
$childPid = $child.Id

$reads += Read-Pdh
Start-Sleep -Seconds 10
$reads += Read-Pdh

$code = Wait-PoolWatch $pw 180
Assert-True ($code -eq 0) "headless exit code 0 (got $code)"

$totals = Import-PwCsv "$Out\totals.csv"
Assert-True ($totals.Count -ge 10) "totals.csv has >= 10 samples ($($totals.Count))"
# Each PDH read against the totals.csv row nearest to it in time, both in UTC: totals.csv writes ISO 8601 times with a
# Z, which DateTimeOffset parses to the same instant whatever the runner's time zone or culture.
$inv = [Globalization.CultureInfo]::InvariantCulture
$rows = @($totals | ForEach-Object {
    [pscustomobject]@{ At = [DateTimeOffset]::Parse($_.time, $inv).UtcDateTime; Paged = [double]$_.committed_paged_bytes; NonPaged = [double]$_.committed_nonpaged_bytes }
})
$pairs = @(foreach ($r in $reads) {
    $near = $rows | Sort-Object { [math]::Abs(($_.At - $r.At).TotalSeconds) } | Select-Object -First 1
    [pscustomobject]@{ Read = $r; Row = $near; Gap = [math]::Abs(($near.At - $r.At).TotalSeconds) }
})
# Paged and nonpaged are judged separately, and each needs two pairs within 25%: one spike, in a read or in a sample,
# spoils one pair only, while a real disagreement shows in every pair. A pair whose nearest row is more than 10 s from
# the read (samples missed, or the read after PoolWatch stopped) does not count.
foreach ($kind in 'Paged', 'NonPaged') {
    $agree = 0
    $notes = foreach ($p in $pairs) {
        $ref = $p.Read.$kind; $got = $p.Row.$kind
        $ok = $p.Gap -le 10 -and [math]::Abs($got - $ref) / $ref -lt 0.25
        if ($ok) { $agree++ }
        $verdict = if ($p.Gap -gt 10) { 'too far, not counted' } elseif ($ok) { 'agrees' } else { 'off' }
        '{0:N0} MB vs PDH {1:N0} MB at {2:HH:mm:ss}Z, {3:N1} s apart ({4})' -f ($got / 1MB), ($ref / 1MB), $p.Read.At, $p.Gap, $verdict
    }
    Assert-True ($agree -ge 2) ("committed $($kind.ToLower()) within 25% of PDH in $agree of $($pairs.Count) reads (2 needed): " + ($notes -join '; '))
}
$mid = $totals[[int]($totals.Count / 2)]
$paged = [double]$mid.committed_paged_bytes; $nonPaged = [double]$mid.committed_nonpaged_bytes
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
