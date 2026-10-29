# Installer end to end (CI only: the runner is an elevated, throwaway VM). Pass 1: a silent install and checks of what it
# installed; a real change made with the installed PoolWatch (a harmless optional service turned off) and a logon task;
# PoolWatch left running; then a silent uninstall, which must stop PoolWatch through its exit signal (never a kill), list
# what it puts back (its log names each change), put everything back without asking and keep the data folder. Pass 2: an
# upgrade while PoolWatch runs, which must stop it the same way; then the same change, uninstalled with /KEEPCHANGES: the
# change and its record stay. Last, a reinstall and a silent uninstall with /DELETEDATA, which must put the kept change
# back by itself (nothing else restores it) and then remove the data folder. Every uninstall here is silent, so what only
# an uninstall started by hand shows is not run here: the question before the cleanup, the notices after it, and what a
# /DELETEDATA uninstall started by hand says (tests/PoolWatch.AppTests/UninstallerTests.cs holds those parts of the script).
# A silent install still walks the setup's pages, so the first install's log also shows that the Welcome and Ready
# pages' own code ran: where each page's text ends, that it fits on the page, and that every icon could be shown. When the
# setup guide that the running PoolWatch opens runs the token-leak check, each of its helper runs must have got a job that
# ends it with PoolWatch (PoolWatch's log says which); a run in which the check started no helper is said, not failed.
param(
    [Parameter(Mandatory)] [string]$Setup,
    [Parameter(Mandatory)] [string]$Out
)
. "$PSScriptRoot\common.ps1"
New-Item -ItemType Directory -Force $Out | Out-Null

$app = Join-Path $env:ProgramFiles 'PoolWatch'
$exe = Join-Path $app 'PoolWatch.exe'
$data = Join-Path $env:ProgramData 'PoolWatch'
$log = Join-Path $data 'poolwatch.log'
$startMenu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\PoolWatch.lnk'
$appId = (Select-String -Path "$PSScriptRoot\..\installer\PoolWatch.iss" -Pattern '^AppId=\{\{([0-9A-F-]+)\}').Matches[0].Groups[1].Value
$uninstallKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{$appId}_is1"
$silent = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
# The lines the setup's page code logs (LayOutWelcomePage and LayOutReadyPage in PoolWatch.iss; InstallerPagesTests
# holds these patterns to the script's own lines): the page's width and height, then where its parts are.
$welcomeLine = 'Welcome page, (-?\d+) x (-?\d+): the text ends at (-?\d+)\.'
$readyLine = 'Ready page, (-?\d+) x (-?\d+): steps end at (-?\d+), the panel runs from (-?\d+) to (-?\d+), the last line runs from (-?\d+) to (-?\d+)\.'
# The lines PoolWatch logs (poolwatch.log) for each helper run of a token-leak check that only checks, such as the setup
# guide's: it got a job that ends it with PoolWatch (TokenLeakGuard.RunHelper), or Windows refused the job and the helper
# runs as a plain process that may outlive PoolWatch (TokenLeakGuard.StartHelper). TokenLeakHelperTests holds these
# patterns to the lines PoolWatch logs.
$helperInJob = 'helper runs in a job'
$helperNoJob = 'helper runs without a job'

# When a setup failed or an uninstall left the install folder: what is left in it (each file and folder with its size
# and attributes) and every process that runs from it, in the job's log and in <step>-left.txt, and PoolWatch's own log
# so far (poolwatch-<step>.log), both in the output folder that CI uploads.
function Show-InstallFolder([string]$name) {
    $lines = @()
    if (Test-Path -LiteralPath $app) {
        $lines += "left in ${app}:"
        foreach ($item in @(Get-ChildItem -LiteralPath $app -Recurse -Force -ErrorAction SilentlyContinue)) {
            $size = if ($item.PSIsContainer) { 'folder' } else { "$($item.Length) bytes" }
            $lines += "  $($item.FullName.Substring($app.Length + 1)): $size, $($item.Attributes)"
        }
    }
    else { $lines += "$app is not there" }
    $running = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith("$app\", [StringComparison]::OrdinalIgnoreCase) })
    foreach ($r in $running) {
        $lines += "  runs from it: process $($r.ProcessId) (parent $($r.ParentProcessId), started $($r.CreationDate)): $($r.CommandLine)"
    }
    if ($running.Count -eq 0) { $lines += '  no process runs from it' }
    $lines | ForEach-Object { Write-Host "  $_" }
    Set-Content -LiteralPath "$Out\$name-left.txt" -Value $lines -ErrorAction SilentlyContinue
    Copy-Item -LiteralPath $log "$Out\poolwatch-$name.log" -ErrorAction SilentlyContinue
}

# What a setup or uninstall waited for before it touched a file: the lines its log has about PoolWatch processes in the
# install folder (NoneRunsFromApp in PoolWatch.iss names each one with its command line, then says how the wait ended),
# echoed into the job's log. Not a check: it shows why a step passed or failed.
function Show-Waits([string]$name) {
    $waits = @(@(Get-Content "$Out\$name.log" -ErrorAction SilentlyContinue) -match 'the install folder')
    if ($waits.Count -eq 0) { Write-Host "  ${name}: its log names no PoolWatch process that it waited for" }
    foreach ($line in $waits) { Write-Host "  ${name}: $line" }
}

# Every setup and uninstall writes its own log (/LOG) to the output folder, which CI uploads (installer-e2e).
function Install([string]$name) {
    $p = Start-Process $Setup -ArgumentList ($silent + '/SP-' + "/LOG=`"$Out\$name.log`"") -Wait -PassThru
    Show-Waits $name
    Assert-True ($p.ExitCode -eq 0) "${name}: setup exit code 0 (got $($p.ExitCode))"
    if ($p.ExitCode -ne 0) { Show-InstallFolder $name }
}

function Uninstall([string]$name, [string[]]$switches = @()) {
    $p = Start-Process (Join-Path $app 'unins000.exe') -ArgumentList ($silent + $switches + "/LOG=`"$Out\$name.log`"") -Wait -PassThru
    # Its first copy returns once the files are removed; the second (_unins.tmp, in TEMP) may still be finishing.
    $deadline = (Get-Date).AddMinutes(2)
    while ((Get-CimInstance Win32_Process -Filter "Name LIKE '[_]unins%'") -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
    Show-Waits $name
    Assert-True ($p.ExitCode -eq 0) "${name}: uninstall exit code 0 (got $($p.ExitCode))"
}

# The installed PoolWatch, started WITHOUT --allow-any-location: the install folder must pass its own location check.
function Invoke-Installed([string]$name, [string[]]$arguments) {
    $p = Start-Process $exe -ArgumentList $arguments -Wait -PassThru -NoNewWindow -RedirectStandardOutput "$Out\$name.txt"
    Write-Host "  $name -> exit $($p.ExitCode): $(Get-Content "$Out\$name.txt" -Raw)"
    return $p.ExitCode
}

function Get-StartMode([string]$service) { (Get-CimInstance Win32_Service -Filter "Name='$service'").StartMode }

# The uninstaller's own log (/LOG): what it listed and what it chose.
function Get-UninstallLog([string]$name) { "$(Get-Content "$Out\$name.log" -Raw -ErrorAction SilentlyContinue)" }

# Starts the installed tray app as the logon task does (--tray) and waits until it runs and samples. --tray opens no main
# window, but while its setup guide was never completed, as on this runner, PoolWatch opens the setup guide, which on
# Windows build 26100 and later runs the token-leak check (a hidden helper, PoolWatch.exe --token-leak-test). Returns the process and the number of
# log lines before it started, for Assert-StoppedBySignal.
function Start-Running([string]$name) {
    $skip = @(Get-Content $log -ErrorAction SilentlyContinue).Count
    $p = Start-Process $exe -ArgumentList '--tray' -PassThru -RedirectStandardOutput "$Out\$name.txt"
    $null = $p.Handle   # keeps the exit code readable after the process ends
    $deadline = (Get-Date).AddSeconds(90)
    do {
        Start-Sleep -Seconds 2
        $started = @(Get-Content $log -ErrorAction SilentlyContinue | Select-Object -Skip $skip) -match ' UI; '
    } until ($started -or $p.HasExited -or (Get-Date) -gt $deadline)
    Assert-True ($started -and -not $p.HasExited) "${name}: the installed PoolWatch is running"
    Start-Sleep -Seconds 10   # past start-up, sampling
    return [pscustomobject]@{ Process = $p; Skip = $skip }
}

# The PoolWatch that Start-Running started was stopped by $by through its exit signal: its log says so, and it exited
# cleanly on its own (never a kill).
function Assert-StoppedBySignal($running, [string]$by) {
    Assert-True ($running.Process.WaitForExit(30000)) "PoolWatch was stopped by the $by"
    $tail = @(Get-Content $log | Select-Object -Skip $running.Skip)
    Assert-True (@($tail -match 'exit signal: exit requested').Count -gt 0) "the $by stopped PoolWatch through its exit signal (its log says so)"
    Assert-True ($running.Process.ExitCode -eq 0) "PoolWatch exited cleanly after the $by (exit $($running.Process.ExitCode))"
}

# The setup guide's token-leak check, in the PoolWatch Start-Running started: each of its helper runs got a job, which
# PoolWatch ends as it exits. Without one the helper may outlive PoolWatch, and only the installer's wait would cover it.
# A run of PoolWatch in which the check started no helper is said in the job's log, not failed.
function Assert-HelperInJob($running, [string]$name) {
    $tail = @(Get-Content $log -ErrorAction SilentlyContinue | Select-Object -Skip $running.Skip)
    $inJob = @($tail -match $helperInJob)
    $noJob = @($tail -match $helperNoJob)
    if ($inJob.Count + $noJob.Count -eq 0) {
        Write-Host "  ${name}: the token-leak check started no helper in this run of PoolWatch, so its job is not checked"
        return
    }
    foreach ($line in $noJob) { Write-Host "  ${name}: $line" }
    Assert-True ($noJob.Count -eq 0) "${name}: each helper run of the token-leak check got a job ($($inJob.Count) in a job, $($noJob.Count) without)"
}

# A harmless catalogue service on this runner (the candidates ci/diag-e2e.ps1 uses).
$service = foreach ($name in 'MapsBroker', 'Fax', 'RetailDemo', 'WMPNetworkSvc', 'WalletService', 'PhoneSvc', 'icssvc') {
    $s = Get-Service $name -ErrorAction SilentlyContinue
    if ($s -and $s.StartType -ne 'Disabled') { $name; break }
}
if (-not $service) { throw 'No harmless catalogue service on this runner to change' }
$before = Get-StartMode $service
Write-Host "service: $service (StartMode $before)"

Write-Host '--- Pass 1: install, change, uninstall while PoolWatch runs'
Install 'install-1'
# The pages' code ran, at the runner's display scale: each page's text ends inside the page, the Ready page's parts are
# in order (steps, then the panel, then "Click Install to continue."), and every icon on it could be shown.
$installLog = "$(Get-Content "$Out\install-1.log" -Raw -ErrorAction SilentlyContinue)"
$welcome = [regex]::Match($installLog, $welcomeLine)
Assert-True ($welcome.Success -and [int]$welcome.Groups[3].Value -le [int]$welcome.Groups[2].Value) "the Welcome page's text fits on the page ($($welcome.Value))"
$ready = [regex]::Match($installLog, $readyLine)
$fits = $ready.Success -and [int]$ready.Groups[3].Value -lt [int]$ready.Groups[4].Value -and
    [int]$ready.Groups[5].Value -le [int]$ready.Groups[6].Value -and [int]$ready.Groups[7].Value -le [int]$ready.Groups[2].Value
Assert-True $fits "the Ready page's steps, panel and last line are in order and fit on the page ($($ready.Value))"
Assert-True ($installLog -notmatch "The Ready page's icon \S+ could not be shown") 'the setup showed every icon on its Ready page'
foreach ($f in 'PoolWatch.exe', 'unins000.exe', 'chatgpt-no-shader-cache.ps1', 'AGENTS.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md',
               'amd64\KernelTraceControl.dll', 'licenses\Microsoft.NETCore.App\LICENSE.TXT') {
    Assert-True (Test-Path (Join-Path $app $f)) "installed $f"
}
Assert-True (-not (Test-Path (Join-Path $app 'PoolWatch-selfcontained.exe'))) 'the self-contained exe is installed as PoolWatch.exe'
Assert-True (Test-Path $startMenu) 'Start menu entry'
$entry = Get-ItemProperty $uninstallKey -ErrorAction SilentlyContinue
Assert-True ($entry.DisplayName -eq 'PoolWatch') "Uninstall entry ($($entry.DisplayName) $($entry.DisplayVersion))"

$code = Invoke-Installed 'selftest' @('--selftest', '--out', "$Out\selftest")
Assert-True ($code -eq 0) "the installed PoolWatch runs from its install folder (selftest exit $code)"
$code = Invoke-Installed 'service-off-1' @('--service-off', $service)
Assert-True ($code -eq 0 -and (Get-StartMode $service) -eq 'Disabled') "the installed PoolWatch turned $service off (exit $code)"
$code = Invoke-Installed 'changes-dump' @('--changes-dump', "$Out\changes.json", '--out', $Out)
$changes = @(Get-Content "$Out\changes.json" -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json)
Assert-True ($code -eq 0 -and @($changes | Where-Object { $_.id -eq "service:$($service.ToLowerInvariant())" }).Count -eq 1) "--changes-dump lists $service (exit $code)"
# A logon task like the one Settings registers: the uninstaller must remove it.
Register-ScheduledTask -TaskName 'PoolWatch' -TaskPath '\' -Action (New-ScheduledTaskAction -Execute $exe -Argument '--tray') `
    -Trigger (New-ScheduledTaskTrigger -AtLogOn) -RunLevel Highest -Force | Out-Null

$pw = Start-Running 'tray-1'
Uninstall 'uninstall-1'
Assert-StoppedBySignal $pw 'uninstaller'
Assert-HelperInJob $pw 'tray-1'
Assert-True ((Get-StartMode $service) -eq $before) "the uninstaller put $service back to $before (got $(Get-StartMode $service))"
Assert-True ($null -eq (Get-ScheduledTask -TaskName 'PoolWatch' -TaskPath '\' -ErrorAction SilentlyContinue)) 'the logon task is gone'
Assert-True (-not (Test-Path $app)) 'the install folder is gone'
if (Test-Path $app) { Show-InstallFolder 'uninstall-1' }
Assert-True (-not (Test-Path $startMenu)) 'the Start menu entry is gone'
Assert-True (-not (Test-Path $uninstallKey)) 'the Uninstall entry is gone'
$summary = Get-Content (Join-Path $data 'uninstall-summary.txt') -Raw -ErrorAction SilentlyContinue
Write-Host $summary
Assert-True ($summary -match "(?m)^Put back: .*\b$([regex]::Escape($service))\b") "uninstall-summary.txt lists $service as put back"
Assert-True (Test-Path $data) 'the data folder is kept'
$uninstallLog = Get-UninstallLog 'uninstall-1'
Assert-True ($uninstallLog -notmatch 'could not list its changes') 'the uninstaller read the list of changes from the installed PoolWatch'
Assert-True ($uninstallLog -match "Uninstalling puts back: Turned off .*\b$([regex]::Escape($service))\b") "the uninstaller listed $service as one it puts back"
Assert-True ($uninstallLog -match 'Uninstalling puts back: PoolWatch starts when you sign in') 'the uninstaller listed the logon task as one it puts back'
Assert-True ($uninstallLog -match 'Every change is put back\.') 'a silent uninstall puts every change back, without asking'
Assert-True (-not (Test-Path (Join-Path $data 'uninstall-list.json'))) 'the uninstaller deleted the list it read'

Write-Host '--- Pass 2: an upgrade while PoolWatch runs, then an uninstall with /KEEPCHANGES'
Install 'install-2'
$pw = Start-Running 'tray-2'
Install 'upgrade-2'
Assert-StoppedBySignal $pw 'upgrade'
Assert-HelperInJob $pw 'tray-2'
Assert-True ((Test-Path $exe) -and (Test-Path (Join-Path $app 'unins000.exe')) -and (Test-Path $uninstallKey)) 'the upgrade left PoolWatch installed'
$code = Invoke-Installed 'service-off-2' @('--service-off', $service)
Assert-True ($code -eq 0 -and (Get-StartMode $service) -eq 'Disabled') "the installed PoolWatch turned $service off again (exit $code)"
Uninstall 'uninstall-2' @('/KEEPCHANGES')
Assert-True ((Get-StartMode $service) -eq 'Disabled') "/KEEPCHANGES left $service off"
$saved = @(Get-Content (Join-Path $data 'service-changes.json') -Raw | ConvertFrom-Json)
Assert-True (@($saved | Where-Object { $_.service -eq $service }).Count -eq 1) "/KEEPCHANGES kept the record of $service"
$summary = Get-Content (Join-Path $data 'uninstall-summary.txt') -Raw -ErrorAction SilentlyContinue
Write-Host $summary
Assert-True ($summary -match "(?m)^Kept: .*\b$([regex]::Escape($service))\b") "uninstall-summary.txt lists $service as kept"
Assert-True ((Get-UninstallLog 'uninstall-2') -match 'The changes are kept') '/KEEPCHANGES kept the changes, without asking'
Assert-True (-not (Test-Path $app)) 'the install folder is gone'
if (Test-Path $app) { Show-InstallFolder 'uninstall-2' }

Write-Host '--- Last: a reinstall, then a silent /DELETEDATA uninstall puts the kept change back by itself and removes the data folder'
Install 'install-3'
Assert-True ((Get-StartMode $service) -eq 'Disabled') "$service is still off, as /KEEPCHANGES left it"
Copy-Item -LiteralPath $log "$Out\poolwatch.log" -ErrorAction SilentlyContinue   # uploaded: /DELETEDATA deletes it
Uninstall 'uninstall-3' @('/DELETEDATA')
$uninstallLog = Get-UninstallLog 'uninstall-3'
Assert-True ($uninstallLog -match "Uninstalling puts back: Turned off .*\b$([regex]::Escape($service))\b") "the silent /DELETEDATA uninstall listed $service as one it puts back"
Assert-True ($uninstallLog -match 'Every change is put back\.') 'a silent /DELETEDATA uninstall puts every change back, without asking'
Assert-True ((Get-StartMode $service) -eq $before) "the silent /DELETEDATA uninstall put $service back to $before (got $(Get-StartMode $service))"
Assert-True (-not (Test-Path $app)) 'the install folder is gone after the silent /DELETEDATA uninstall'
if (Test-Path $app) { Show-InstallFolder 'uninstall-3' }
Assert-True (-not (Test-Path $data)) 'the silent /DELETEDATA uninstall removed the data folder once every change was put back'

Complete-Assertions 'Installer e2e'
