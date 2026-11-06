# Installer end to end (CI only: the runner is an elevated, throwaway VM). Pass 1: a silent install and checks of what it
# installed; a real change made with the installed PoolWatch (a harmless optional service turned off) and a logon task;
# PoolWatch left running; then a silent uninstall, which must stop PoolWatch through its exit signal (never a kill), list
# what it puts back (its log names each change), put everything back without asking and keep the data folder. Pass 2: an
# upgrade while PoolWatch runs, which must stop it the same way; an upgrade while a process started through the short
# (8.3) spelling of the install folder runs the exe, and one while a process started through a junction to the folder
# runs it: each must finish at the next restart (exit 3010, Windows' list of renames for that restart moving every new
# file into place, no restart of the runner although /NORESTART is not given, the process named by its exe's path),
# after which this test does what Windows would do at that restart; one while a token-leak test helper started through
# the short spelling runs, which must end it and go on; one while a second process holds a file of PoolWatch's open
# without letting it be renamed or deleted, which must finish at the next restart and name that process, then the setup
# again before that restart, which must finish at the restart too, after the first (never Inno Setup's own "restart
# now"), with no restart of the runner; one while a second process holds the uninstaller's own unins000.dat, which must
# replace the files now and write its uninstaller in a folder of its own; then the same change, uninstalled with
# /KEEPCHANGES: the change and its record stay. Last, a reinstall; an upgrade that fails after every file was written
# (its Uninstall entry cannot be written), which must leave the version that was there whole, its uninstaller included;
# the same while a second process holds one of the new files open without letting it be renamed or deleted, which must
# leave no launcher and no PoolWatch itself until the restart and every old file back after the restart's work; one that
# fails after Inno Setup wrote the new Uninstall entry (a second process holds the uninstaller's record), which must
# write the old entry back as it was; one during which a second process holds an old file the setup has just set aside,
# sharing it with no one, which must put every old file back (that one under a second name of the same file), finish at
# the next restart and name that process; a silent uninstall whose cleanup cannot be started, which must remove no
# file and put nothing back; a silent uninstall with /DELETEDATA while a process runs the exe, which must put the
# kept change back by itself (nothing else restores it), choose to run its cleanup a second time, remove everything
# but the exe in use, leave that and the folder to Windows to delete at the next restart, and remove the data folder;
# and a setup before that restart, which must finish at the restart after the uninstall's deletes and leave a whole
# install once that restart's work is done. Every uninstall here is silent, so what only an uninstall started
# by hand shows is not run here: the question before the cleanup, the notices after it, and what a /DELETEDATA
# uninstall started by hand says (tests/PoolWatch.AppTests/UninstallerTests.cs holds those parts of the script).
# A silent install still walks the setup's pages, so the first install's log also shows that the Welcome and Ready
# pages' own code ran: where each page's text ends, that it fits on the page, and that every icon could be shown. When the
# setup guide that the running PoolWatch opens runs the token-leak check, each of its helper runs must have got a job that
# ends it with PoolWatch (PoolWatch's log says which); a run in which the check started no helper is said, not failed, but
# CI fails when neither run had a helper to check. A CHECK-RAN line follows each check the tests pin: in its own block,
# the statement right after it or right after the last check of the run of checks it is in, or the statement right
# after the try statement (with a finally and no catch) whose body holds it. CI fails a run of this script that does not
# print each one ci/expected-checks.txt lists for it.
# PoolWatch is started here as its users start it, through its launcher (the install folder's PoolWatch.exe, which
# passes on the arguments and the exit code); one run starts PoolWatch itself (app\PoolWatch.exe), as an administrator's
# own console would, which must start again through the launcher and pass its output on. The Start menu entry starts
# the launcher. Before all that, Pass 0: 1.0.0-beta.1's own setup (pinned by its release's SHA-256) installs the layout
# before the launcher, with a logon task as that version made it; an upgrade of it that fails while a second process
# holds one of the new files open without letting it be renamed or deleted must leave no PoolWatch.exe and that
# version's uninstaller unable to remove anything until the restart (started then, it stops at once, exit 1, without
# a log), and every file of that version back after the restart's work; the same while a third process holds that
# uninstaller's record (its messages file is set aside instead, and that uninstaller, started once the record is free,
# stops the same way), while it holds the record and the messages file (its program is set aside instead, so the
# Uninstall entry starts nothing until the restart), and while it holds all three (the setup says not to uninstall
# before the restart). Then it runs; the setup upgrades it, which must stop it through its own exit signal without
# starting it, leave no file of that layout, and leave the task starting the install folder's PoolWatch.exe, which is
# the launcher now. Pass 0b: 1.0.0-beta.1's self-contained zip (pinned the same way) unpacked into the install folder,
# then the setup, which must put the launcher at that zip's PoolWatch-selfcontained.exe too; PoolWatch started under
# that name, then the setup again, which must stop it through its exit signal and finish now.
# After the reinstall, PoolWatch is started once through Task Scheduler, as the logon task starts it (through its
# launcher), and each helper run must still get a job of its own; then PoolWatch itself (app\PoolWatch.exe, below its
# launcher) is killed while that check runs (a run in which no helper was seen fails), and no helper may run on after it.
param(
    [Parameter(Mandatory)] [string]$Setup,
    [Parameter(Mandatory)] [string]$Out
)
. "$PSScriptRoot\common.ps1"
New-Item -ItemType Directory -Force $Out | Out-Null

$app = Join-Path $env:ProgramFiles 'PoolWatch'
# PoolWatch itself, below its launcher, which has PoolWatch's own name in the install folder.
$exe = Join-Path $app 'app\PoolWatch.exe'
$launcher = Join-Path $app 'PoolWatch.exe'
$data = Join-Path $env:ProgramData 'PoolWatch'
$log = Join-Path $data 'poolwatch.log'
$startMenu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\PoolWatch.lnk'
$appId = (Select-String -Path "$PSScriptRoot\..\installer\PoolWatch.iss" -Pattern '^AppId=\{\{([0-9A-F-]+)\}').Matches[0].Groups[1].Value
$uninstallSubKey = "SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{$appId}_is1"
$uninstallKey = "HKLM:\$uninstallSubKey"
$silent = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
# The lines the setup's page code logs (LayOutWelcomePage and LayOutReadyPage in PoolWatch.iss; InstallerPagesTests
# holds these patterns to the script's own lines): the page's width and height, then where its parts are.
$welcomeLine = 'Welcome page, (-?\d+) x (-?\d+): the text ends at (-?\d+)\.'
$readyLine = 'Ready page, (-?\d+) x (-?\d+): steps end at (-?\d+), the panel runs from (-?\d+) to (-?\d+), the last line runs from (-?\d+) to (-?\d+)\.'
# The lines PoolWatch logs (poolwatch.log) for each helper run of a token-leak check that only checks, such as the setup
# guide's: it got a job that ends it with PoolWatch, which PoolWatch saw it in (TokenLeakGuard.RunHelper), or Windows
# would not start it in a job, so it was not started at all and that run of the check failed (TokenLeakGuard.StartHelper).
# TokenLeakHelperTests holds these patterns to the lines PoolWatch logs.
$helperInJob = 'helper runs in a job'
$helperNoJob = 'helper could not start in a job'

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
# install folder (WaitForApp in PoolWatch.iss names each one with its command line, then says how the wait ended),
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

# The uninstaller Apps and Features starts (the Uninstall entry's UninstallString, its quoted path then /LOG): the install
# folder's unins000.exe, or, after a setup that could not write it there, the one in a folder of that setup's own below
# uninstall\.
function Get-Uninstaller {
    $command = "$((Get-ItemProperty $uninstallKey -ErrorAction SilentlyContinue).UninstallString)"
    if ($command -match '^"([^"]+)"') { return $Matches[1] }
    return $command
}

# The Uninstall entry's values, by name (each as text), for comparing the entry before and after a step.
function Get-Entry {
    $values = @{}
    $entry = Get-ItemProperty -LiteralPath $uninstallKey -ErrorAction SilentlyContinue
    if ($entry) { foreach ($p in $entry.PSObject.Properties) { if ($p.Name -notlike 'PS*') { $values[$p.Name] = "$($p.Value)" } } }
    return $values
}

function Uninstall([string]$name, [string[]]$switches = @(), [string[]]$quiet = $silent) {
    $p = Start-Process (Get-Uninstaller) -ArgumentList ($quiet + $switches + "/LOG=`"$Out\$name.log`"") -Wait -PassThru
    # Its first copy returns once the files are removed; the second (_unins.tmp, in a folder of its own in TEMP) may
    # still be finishing.
    $deadline = (Get-Date).AddMinutes(2)
    while ((Get-CimInstance Win32_Process -Filter "Name LIKE '[_]unins%'") -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
    Show-Waits $name
    Assert-True ($p.ExitCode -eq 0) "${name}: uninstall exit code 0 (got $($p.ExitCode))"
}

# The installed PoolWatch, started WITHOUT --allow-any-location: the install folder must pass its own location check.
# Through its launcher, unless -Program names PoolWatch itself (app\PoolWatch.exe).
function Invoke-Installed([string]$name, [string[]]$arguments, [string]$program = $launcher) {
    $p = Start-Process $program -ArgumentList $arguments -Wait -PassThru -NoNewWindow -RedirectStandardOutput "$Out\$name.txt"
    Write-Host "  $name -> exit $($p.ExitCode): $(Get-Content "$Out\$name.txt" -Raw)"
    return $p.ExitCode
}

function Get-StartMode([string]$service) { (Get-CimInstance Win32_Service -Filter "Name='$service'").StartMode }

# The uninstaller's own log (/LOG): what it listed and what it chose.
function Get-UninstallLog([string]$name) { "$(Get-Content "$Out\$name.log" -Raw -ErrorAction SilentlyContinue)" }

# Starts the installed tray app as the logon task does (the launcher, PoolWatch.exe --tray, or the launcher under the name
# $via gives) and waits until it runs and samples. --tray opens no main window, but while its setup guide was never
# completed, as on this runner, PoolWatch opens the setup guide, which on Windows build 26100 and later runs the
# token-leak check (a hidden helper, app\PoolWatch.exe --token-leak-test). Returns the process (the launcher, which runs
# until PoolWatch ends and ends with its exit code) and the number of log lines before it started, for
# Assert-StoppedBySignal.
function Start-Running([string]$name, [string]$via = $launcher) {
    $skip = @(Get-Content $log -ErrorAction SilentlyContinue).Count
    $p = Start-Process $via -ArgumentList '--tray' -PassThru -RedirectStandardOutput "$Out\$name.txt"
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
    $tail = @(Get-Content $log -ErrorAction SilentlyContinue | Select-Object -Skip $running.Skip)
    Assert-True (@($tail -match 'exit signal: exit requested').Count -gt 0) "the $by stopped PoolWatch through its exit signal (its log says so)"
    Assert-True ($running.Process.ExitCode -eq 0) "PoolWatch exited cleanly after the $by (exit $($running.Process.ExitCode))"
}

# The setup guide's token-leak check, in the PoolWatch Start-Running started: each of its helper runs got a job, which
# PoolWatch ends as it exits. A run Windows would not start in a job is not started (that run of the check fails), so a
# line saying so means the job start does not work on this Windows.
# A run of PoolWatch in which the check started no helper is said in the job's log, not failed; its marker is printed only
# by a run whose helpers were checked, so CI fails when no run was.
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
    Write-Host 'CHECK-RAN: installer-helper-runs-in-a-job'
}

# The installed exe started through another spelling of its path, suspended: Windows has mapped the exe, so the file is
# in use and the process is in Windows' list, but none of PoolWatch's code runs (CREATE_SUSPENDED), so it neither holds
# PoolWatch's mutex nor hears its exit signal. The steps that start one end it themselves (Stop-Held), through the
# process StartSuspended gives: it holds a handle to that process, taken while CreateProcessW's own was still open, so
# no other process can get its ID while the script keeps it. ShortPath is the path's short (8.3) spelling
# (GetShortPathNameW).
Add-Type -IgnoreWarnings -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public static class HeldExe
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct StartupInfo
    {
        public int cb;
        public string reserved, desktop, title;
        public int x, y, xSize, ySize, xCountChars, yCountChars, fillAttribute, flags;
        public short showWindow, reserved2Size;
        public IntPtr reserved2, stdInput, stdOutput, stdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public IntPtr process, thread;
        public int processId, threadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory,
        ref StartupInfo startup, out ProcessInformation information);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetShortPathNameW(string longPath, StringBuilder shortPath, int size);

    public static string ShortPath(string path)
    {
        var buffer = new StringBuilder(1024);
        int length = GetShortPathNameW(path, buffer, buffer.Capacity);
        if (length == 0 || length >= buffer.Capacity) throw new Win32Exception();
        return buffer.ToString();
    }

    public static Process StartSuspended(string exe, string arguments)
    {
        var startup = new StartupInfo();
        startup.cb = Marshal.SizeOf(typeof(StartupInfo));
        ProcessInformation information;
        if (!CreateProcessW(exe, new StringBuilder("\"" + exe + "\" " + arguments), IntPtr.Zero, IntPtr.Zero, false,
                0x4 /* CREATE_SUSPENDED */, IntPtr.Zero, null, ref startup, out information))
            throw new Win32Exception();
        CloseHandle(information.thread);
        try
        {
            var process = Process.GetProcessById(information.processId);
            IntPtr held = process.Handle;
            return process;
        }
        finally { CloseHandle(information.process); }
    }
}
'@

# What the setup and the uninstaller give Windows to do at its next restart (MoveFileEx's restart-time rename, which
# Windows keeps in the Session Manager's PendingFileRenameOperations value, and ...2 when that one is full): the entries
# for paths in the install folder, each with its value, the path it moves (From) and where to (To; '' to delete).
$sessionManager = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager'
# One of those two values as its strings in order (a delete's empty target kept), or none when the value is not there.
# Each function here writes its items one by one and each caller collects them with @(...): an array written whole
# (return , $list) would be collected as one item, and the count would be 1 whatever the list held.
function Get-RestartValue([string]$name) {
    $value = (Get-ItemProperty -LiteralPath $sessionManager -Name $name -ErrorAction SilentlyContinue).$name
    if ($null -eq $value) { return }
    return @($value)
}
# A path as Windows' list writes it, without what may come before it, as the setup reads it (ListedPath): the ! of a
# move that may replace a file, the *1 or *2 that recent Windows builds put first (every entry on a Windows 11 25H2 PC
# has one), and the \??\.
function Get-ListedPath([string]$item) {
    return ($item -replace '^!', '' -replace '^\*[12]', '' -replace '^\\\?\?\\', '')
}
function Get-RestartWork {
    $work = @()
    foreach ($name in 'PendingFileRenameOperations', 'PendingFileRenameOperations2') {
        $ops = @(Get-RestartValue $name)
        for ($i = 0; $i + 1 -lt $ops.Count; $i += 2) {
            $from = Get-ListedPath $ops[$i]
            $to = Get-ListedPath $ops[$i + 1]
            if ($from.StartsWith($app, [StringComparison]::OrdinalIgnoreCase)) {
                $work += [pscustomobject]@{ Value = $name; From = $from; To = $to }
            }
        }
    }
    return $work
}

# What Windows does at its next restart with that work, in its order: each file moved over its target, or deleted, and
# each folder deleted if it is empty then (Windows leaves one that is not: the setup and the uninstaller give it each
# folder to delete once it is empty). The runner cannot restart within the job, so this stands in for the restart.
# Every entry for the install folder is then taken out of Windows' list, done or not, and any other program's are left
# as they were; as an empty list then does not show that the work was done, this writes out, one by one, each entry
# for the install folder that it could not carry out (one that failed, or a move whose file is not there) as
# "<path> -> '<target>'", for the check after each call to fail on. A folder left because it is not empty is not one.
function Complete-RestartWork {
    foreach ($name in 'PendingFileRenameOperations', 'PendingFileRenameOperations2') {
        $ops = @(Get-RestartValue $name)
        if ($ops.Count -eq 0) { continue }
        $keep = [System.Collections.Generic.List[string]]::new()
        for ($i = 0; $i + 1 -lt $ops.Count; $i += 2) {
            $from = Get-ListedPath $ops[$i]
            $to = Get-ListedPath $ops[$i + 1]
            if (-not $from.StartsWith($app, [StringComparison]::OrdinalIgnoreCase)) { $keep.Add($ops[$i]); $keep.Add($ops[$i + 1]); continue }
            try {
                if ($to -eq '') {
                    if (Test-Path -LiteralPath $from -PathType Container) {
                        if (@(Get-ChildItem -LiteralPath $from -Force).Count -eq 0) { [System.IO.Directory]::Delete($from) }
                        else { Write-Host "  at the restart: $from is not empty, so it stays, as Windows leaves it" }
                    }
                    elseif (Test-Path -LiteralPath $from) { [System.IO.File]::Delete($from) }
                }
                elseif (Test-Path -LiteralPath $from) { [System.IO.File]::Move($from, $to, $true) }
                else { throw "$from is not there" }
            }
            catch {
                Write-Host "  at the restart: $from -> '$to' was not done ($($_.Exception.Message)), as Windows would leave it"
                "$from -> '$to'"
            }
        }
        if ($keep.Count -eq 0) { Remove-ItemProperty -LiteralPath $sessionManager -Name $name }
        else { Set-ItemProperty -LiteralPath $sessionManager -Name $name -Value $keep.ToArray() -Type MultiString }
    }
}

# A setup that must finish at the next restart, run without /NORESTART so that a restart it asked for would restart the
# runner and lose the job: exit 3010, its log says no restart is needed, the process holding PoolWatch's exe
# (app\PoolWatch.exe, the $process StartSuspended gave) is named by its exe's path (process $id), the exe in use is
# named, and Windows' list moves the new app\PoolWatch.exe over the old one from the setup's own folder. The setup
# closed no program: Inno Setup started no Restart Manager session of its own (CloseApplications=no; with one, its log
# names the files it registers with it), and the holder still runs. Then the holder is ended and this test does the
# restart's work: afterwards the install folder holds the new exe and no folder of the setup's own is left.
function Assert-FinishesAtRestart([string]$name, [System.Diagnostics.Process]$process) {
    $p = Start-Process $Setup -ArgumentList (@('/VERYSILENT', '/SUPPRESSMSGBOXES') + '/SP-' + "/LOG=`"$Out\$name.log`"") -Wait -PassThru
    $id = $process.Id
    Show-Waits $name
    $setupLog = Get-UninstallLog $name
    Assert-True ($p.ExitCode -eq 3010) "${name}: the setup finishes at the next restart (exit 3010, got $($p.ExitCode))"
    Assert-True ($setupLog -match 'Need to restart Windows\? No') "${name}: the setup asked for no restart"
    Assert-True (($setupLog -notmatch 'files to register with RestartManager|Shutting down applications using our files') -and -not $process.HasExited) "${name}: the setup closed no program (Inno Setup started no Restart Manager session of its own, and process $id still runs)"
    Assert-True ($setupLog -notmatch 'Found pending rename or delete that matches one of our files') "${name}: Inno Setup's own check found none of the setup's files in Windows' list"
    Assert-True ($setupLog -match "PoolWatch still runs from the install folder: process $id, run by ") "${name}: the setup read the holder's exe and named it (process $id)"
    Assert-True ($setupLog -match [regex]::Escape("In use, so it cannot be replaced or removed now: $exe")) "${name}: the setup named the exe in use"
    Write-Host 'CHECK-RAN: installer-finishes-at-restart-log'
    $moves = @((Get-RestartWork) | Where-Object { $_.To -ieq $exe })
    $staged = if ($moves.Count -gt 0) { $moves[-1].From } else { '' }
    Assert-True ($moves.Count -ge 1 -and $staged -like "$app\at-restart\*\app\PoolWatch.exe" -and (Test-Path -LiteralPath $staged)) "${name}: Windows moves the new app\PoolWatch.exe into place at the next restart ($staged)"
    Write-Host 'CHECK-RAN: installer-finishes-at-restart-moves'
    $hash = if ($staged -and (Test-Path -LiteralPath $staged)) { (Get-FileHash -LiteralPath $staged).Hash } else { '' }
    Stop-Held $process
    $notDone = @(Complete-RestartWork)
    Assert-True ($hash -and (Test-Path -LiteralPath $exe) -and (Get-FileHash -LiteralPath $exe).Hash -eq $hash) "${name}: after the restart's work the new app\PoolWatch.exe is in place"
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $app 'at-restart'))) "${name}: after the restart's work no folder of the setup's own is left"
    Assert-True ($notDone.Count -eq 0 -and @(Get-RestartWork).Count -eq 0) "${name}: the restart's work in the install folder all went through, and Windows has none left ($($notDone -join ', '))"
    Write-Host 'CHECK-RAN: installer-finishes-at-restart'
    if ($p.ExitCode -ne 3010) { Show-InstallFolder $name }
}

# Ends a process a step started, and waits until it is gone, so the exe is free for the next step. It is given the
# process as its start gave it, which holds a handle to it ($p.Handle, taken while it surely ran): Windows gives no
# other process its ID while that handle is open, so one that has ended already (the setup ended it, or an earlier
# Stop-Held did) is seen to have ended and is left as it is, and no other program is ended in its place.
function Stop-Held([System.Diagnostics.Process]$process) {
    if (-not $process.HasExited) { $process.Kill(); $null = $process.WaitForExit(30000) }
}

# A second process (Windows PowerShell) that holds $path open for reading and shares it for reading only: without
# FILE_SHARE_DELETE, so no program can rename or delete the file while it runs, as another account's program that reads
# PoolWatch's files could. Returns the process, with a handle to it held (for Stop-Held), once the file is held (a probe
# that wants the file to itself fails).
function Start-Holder([string]$path) {
    $holder = "`$f = [System.IO.File]::Open('$path', 'Open', 'Read', 'Read'); Start-Sleep -Seconds 1800"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($holder))
    $p = Start-Process powershell.exe -ArgumentList "-NoProfile -NonInteractive -EncodedCommand $encoded" -WindowStyle Hidden -PassThru
    $null = $p.Handle   # keeps its ID from being another process's once it has ended
    $deadline = (Get-Date).AddSeconds(60)
    do {
        Start-Sleep -Milliseconds 500
        $held = $false
        try { [System.IO.File]::Open($path, 'Open', 'ReadWrite', 'None').Dispose() } catch { $held = $true }
    } until ($held -or $p.HasExited -or (Get-Date) -gt $deadline)
    Assert-True ($held -and -not $p.HasExited) "a second process (process $($p.Id)) holds $path"
    return $p
}

# A second process (Windows PowerShell) that waits, polling every few milliseconds, until a file matching $trigger is in
# the install folder itself (a setup that set the old files aside names each <file>.<run>.old), then opens $path, or with
# no $path the first file matching $trigger, for reading, shares it as $share says (Read: no program can rename or delete
# it; ReadWrite: no program can open it to itself; None: no program can rename, delete or read it) and holds it; a file
# that cannot be opened yet (not there, or being written) is tried again. It writes the path it holds to <name>-held.txt
# in the output folder. Returns the process, with a handle to it held (for Stop-Held), once it is watching.
function Start-Watcher([string]$name, [string]$trigger, [string]$path, [string]$share) {
    $ready = "$Out\$name-ready.txt"
    $heldFile = "$Out\$name-held.txt"
    $watcher = @"
Set-Content -LiteralPath '$ready' -Value 'watching'
`$deadline = (Get-Date).AddMinutes(10)
`$f = `$null
while (-not `$f -and (Get-Date) -lt `$deadline) {
    `$found = @([System.IO.Directory]::GetFiles('$app', '$trigger'))
    if (`$found.Count -gt 0) {
        `$target = '$path'
        if (`$target -eq '') { `$target = `$found[0] }
        try { `$f = [System.IO.File]::Open(`$target, 'Open', 'Read', '$share'); Set-Content -LiteralPath '$heldFile' -Value `$target } catch { }
    }
    if (-not `$f) { Start-Sleep -Milliseconds 5 }
}
Start-Sleep -Seconds 1800
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($watcher))
    $p = Start-Process powershell.exe -ArgumentList "-NoProfile -NonInteractive -EncodedCommand $encoded" -WindowStyle Hidden -PassThru
    $null = $p.Handle   # keeps its ID from being another process's once it has ended
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Path -LiteralPath $ready) -and -not $p.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    Assert-True ((Test-Path -LiteralPath $ready) -and -not $p.HasExited) "${name}: a second process (process $($p.Id)) watches for $trigger"
    return $p
}

# The path a watcher (Start-Watcher) holds, or '' when it holds none.
function Get-Watched([string]$name) { "$(Get-Content -LiteralPath "$Out\$name-held.txt" -ErrorAction SilentlyContinue)".Trim() }

# Runs the setup silently with $switches (never /NORESTART here, so that a restart asked for by mistake would restart the
# runner and lose the job), its log in the output folder, and returns its exit code; a setup still running after 5
# minutes is ended and counted as failed.
function Invoke-Setup([string]$name, [string[]]$switches = @('/VERYSILENT', '/SUPPRESSMSGBOXES')) {
    $p = Start-Process $Setup -ArgumentList ($switches + '/SP-' + "/LOG=`"$Out\$name.log`"") -PassThru
    if (-not $p.WaitForExit(300000)) {
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        Assert-True $false "${name}: the setup ended within 5 minutes"
        return -1
    }
    Show-Waits $name
    return $p.ExitCode
}

# Every file of the install folder, the uninstaller's own among them, with its SHA-256: to compare the folder before
# and after a step.
function Get-ProgramFiles {
    $files = @{}
    foreach ($f in @(Get-ChildItem -LiteralPath $app -Recurse -Force -File -ErrorAction SilentlyContinue)) {
        $files[$f.FullName] = (Get-FileHash -LiteralPath $f.FullName).Hash
    }
    return $files
}
# Adds a rule to the Uninstall entry's own access list, or takes it out again. The key is opened through .NET in the
# 64-bit view, where the setup writes it: Set-Acl on the registry path said the key did not exist, while Get-Acl read it.
function Set-UninstallKeyRule([System.Security.AccessControl.RegistryAccessRule]$rule, [bool]$add) {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $key = $base.OpenSubKey($uninstallSubKey, [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree,
            [System.Security.AccessControl.RegistryRights]'ReadPermissions, ChangePermissions')
        if ($null -eq $key) { throw "there is no Uninstall entry at HKLM\$uninstallSubKey" }
        try {
            $acl = $key.GetAccessControl()
            if ($add) { $acl.AddAccessRule($rule) } else { [void]$acl.RemoveAccessRule($rule) }
            $key.SetAccessControl($acl)
        } finally { $key.Dispose() }
    } finally { $base.Dispose() }
}
# Runs the setup as step $name with the deny rule on the Uninstall entry, removed again whatever happens; returns its
# exit code.
function Invoke-SetupThatFails([string]$name) {
    $deny = New-Object System.Security.AccessControl.RegistryAccessRule('BUILTIN\Administrators', 'SetValue, Delete', 'Deny')
    Set-UninstallKeyRule $deny $true
    try { return (Invoke-Setup $name) }
    finally { Set-UninstallKeyRule $deny $false }
}

# The helper processes of a token-leak check: PoolWatch itself (app\PoolWatch.exe) started with --token-leak-test.
function Get-Helpers {
    @(Get-CimInstance Win32_Process -Filter "Name = 'PoolWatch.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like '*--token-leak-test*' })
}

# PoolWatch killed (never its exit signal) while the setup guide's token-leak check runs: Windows closes PoolWatch's
# handle to the check's job, and the job ends the helper with it (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE), so no helper runs
# on. The check runs only without a saved verdict for this build, so that is removed first. PoolWatch is started here,
# through its launcher as its users start it, not through Start-Running, and a helper is looked for from that moment:
# the check can start before PoolWatch logs that it runs, and its two helper runs take about 20 s in all, which
# Start-Running's waits could outlast. What is killed is PoolWatch itself, the launcher's child (app\PoolWatch.exe
# --tray), which holds the job; the launcher, which waits for it, then ends. A run in which no helper was seen tests
# nothing, so it fails.
function Assert-HelperEndsWithKill([string]$name) {
    Remove-Item (Join-Path $data 'cache\token-guard.json') -ErrorAction SilentlyContinue
    $p = Start-Process $launcher -ArgumentList '--tray' -PassThru -RedirectStandardOutput "$Out\$name.txt"
    $null = $p.Handle   # keeps the exit code readable after the process ends
    $deadline = (Get-Date).AddSeconds(120)
    $helpers = @()
    while ($helpers.Count -eq 0 -and -not $p.HasExited -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $helpers = @(Get-Helpers)
    }
    foreach ($h in $helpers) { Write-Host "  ${name}: helper $($h.ProcessId) runs: $($h.CommandLine)" }
    $own = @(Get-CimInstance Win32_Process -Filter "Name = 'PoolWatch.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ParentProcessId -eq $p.Id -and $_.CommandLine -notlike '*--token-leak-test*' })
    foreach ($o in $own) { Write-Host "  ${name}: PoolWatch itself is process $($o.ProcessId): $($o.CommandLine)" }
    Assert-True ($own.Count -eq 1) "${name}: PoolWatch itself ran below its launcher ($($own.Count) found)"
    Write-Host 'CHECK-RAN: installer-kill-pw-below-launcher'
    foreach ($o in $own) { Stop-Process -Id $o.ProcessId -Force -ErrorAction SilentlyContinue }
    Assert-True ($p.WaitForExit(30000)) "${name}: PoolWatch was killed, and its launcher ended with it"
    Assert-True ($helpers.Count -gt 0) "${name}: the token-leak check started a helper while PoolWatch ran, so the kill tests it"
    Write-Host 'CHECK-RAN: installer-kill-launcher-ended'
    if ($helpers.Count -eq 0) { return }
    $deadline = (Get-Date).AddSeconds(15)
    do {
        Start-Sleep -Seconds 1
        $left = @(Get-Helpers)
    } until ($left.Count -eq 0 -or (Get-Date) -gt $deadline)
    foreach ($h in $left) { Write-Host "  ${name}: still running: helper $($h.ProcessId): $($h.CommandLine)" }
    Assert-True ($left.Count -eq 0) "${name}: no token-leak helper ran on after PoolWatch was killed ($($left.Count) left)"
    Write-Host 'CHECK-RAN: installer-helper-ends-with-kill'
}

# PoolWatch started by Task Scheduler, as the logon task starts it (the launcher, PoolWatch.exe --tray), so it runs in a
# job of Task Scheduler's: each helper run of the setup guide's token-leak check must still get a job of its own, nested
# in that one (PoolWatch's log says which; one Windows would not start in its job fails here). Started through a task
# like the one Settings registers (this account signed in interactively, highest privileges, so PoolWatch's check at
# start finds its own task and asks nothing), then stopped through its exit signal (--stop-running) and the task
# removed. A run in which the task did not start PoolWatch, or the check started no helper, is said, not failed: then
# this proves nothing about the nested job.
function Assert-HelperInJobFromTask([string]$name) {
    Remove-Item (Join-Path $data 'cache\token-guard.json') -ErrorAction SilentlyContinue
    $skip = @(Get-Content $log -ErrorAction SilentlyContinue).Count
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Highest
    Register-ScheduledTask -TaskName 'PoolWatch' -TaskPath '\' -Action (New-ScheduledTaskAction -Execute $launcher -Argument '--tray') `
        -Trigger (New-ScheduledTaskTrigger -AtLogOn) -Principal $taskPrincipal -Force | Out-Null
    try { Start-ScheduledTask -TaskName 'PoolWatch' -TaskPath '\' }
    catch { Write-Host "  ${name}: the task could not be started: $($_.Exception.Message)" }
    $deadline = (Get-Date).AddSeconds(120)
    do {
        Start-Sleep -Seconds 2
        $tail = @(Get-Content $log -ErrorAction SilentlyContinue | Select-Object -Skip $skip)
        $runs = @($tail -match $helperInJob).Count + @($tail -match $helperNoJob).Count
    } until ($runs -ge 2 -or (Get-Date) -gt $deadline)
    $started = @($tail -match ' UI; ').Count -gt 0
    Write-Host "  ${name}: the task $(if ($started) { 'started' } else { 'did not start' }) PoolWatch (last result $((Get-ScheduledTaskInfo -TaskName 'PoolWatch' -TaskPath '\').LastTaskResult))"
    $code = Invoke-Installed "$name-stop" @('--stop-running')
    Unregister-ScheduledTask -TaskName 'PoolWatch' -TaskPath '\' -Confirm:$false
    Assert-True ($code -eq 0) "${name}: no PoolWatch runs once it was asked to exit (--stop-running exit $code)"
    Write-Host 'CHECK-RAN: installer-task-pw-stopped'
    $deadline = (Get-Date).AddSeconds(15)
    while (@(Get-Helpers).Count -gt 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
    Assert-True (@(Get-Helpers).Count -eq 0) "${name}: no token-leak helper runs on after that PoolWatch exited"
    Write-Host 'CHECK-RAN: installer-task-helper-ends'
    Assert-HelperInJob ([pscustomobject]@{ Skip = $skip }) $name
}

# A harmless catalogue service on this runner (the candidates ci/diag-e2e.ps1 uses).
$service = foreach ($name in 'MapsBroker', 'Fax', 'RetailDemo', 'WMPNetworkSvc', 'WalletService', 'PhoneSvc', 'icssvc') {
    $s = Get-Service $name -ErrorAction SilentlyContinue
    if ($s -and $s.StartType -ne 'Disabled') { $name; break }
}
if (-not $service) { throw 'No harmless catalogue service on this runner to change' }
$before = Get-StartMode $service
Write-Host "service: $service (StartMode $before)"

Write-Host '--- Pass 0: an upgrade from 1.0.0-beta.1, the layout before the launcher, while it runs'
# 1.0.0-beta.1's own setup, pinned by the SHA-256 its release lists in SHA256SUMS.txt. That version installed PoolWatch
# itself as the install folder's PoolWatch.exe, with its native DLLs in amd64\ beside it, and its logon task started that
# exe: the path that is the launcher's now.
$oldSetup = Join-Path $Out 'PoolWatch-v1.0.0-beta.1-setup-win-x64.exe'
Invoke-WebRequest -Uri 'https://github.com/nubbymong/poolwatch/releases/download/v1.0.0-beta.1/PoolWatch-v1.0.0-beta.1-setup-win-x64.exe' -OutFile $oldSetup
$oldHash = (Get-FileHash -LiteralPath $oldSetup -Algorithm SHA256).Hash
if ($oldHash -ne 'BFB8B9388920A83E02F5413DBAEED5D6840BCA6815C0A8B8A81443BDE4412E2A') { throw "the 1.0.0-beta.1 setup's SHA-256 is $oldHash, not the one its release lists" }
$p = Start-Process $oldSetup -ArgumentList ($silent + '/SP-' + "/LOG=`"$Out\install-beta1.log`"") -Wait -PassThru
Assert-True ($p.ExitCode -eq 0) "1.0.0-beta.1 installed (exit $($p.ExitCode))"
Assert-True ((Test-Path $launcher) -and (Test-Path (Join-Path $app 'amd64\KernelTraceControl.dll')) -and -not (Test-Path $exe)) '1.0.0-beta.1 has the layout before the launcher'
# Its logon task as that version registers it: the install folder's PoolWatch.exe --tray, with highest privileges.
Register-ScheduledTask -TaskName 'PoolWatch' -TaskPath '\' -Action (New-ScheduledTaskAction -Execute $launcher -Argument '--tray') `
    -Trigger (New-ScheduledTaskTrigger -AtLogOn) -RunLevel Highest -Force | Out-Null

# An upgrade from 1.0.0-beta.1 that fails (its Uninstall entry cannot be written) while a second process holds one of
# the new files, app\PoolWatch.exe, which that version does not have, open without letting it be renamed or deleted (it
# opens it once the setup has set the old files aside and written it). The rollback cannot delete that file, nor the
# setup move it out of the way, so Windows deletes it at the next restart, and every file of 1.0.0-beta.1 goes back only
# then, its PoolWatch.exe last. That version's own uninstaller would not wait for the restart (it goes on without its
# cleanup when its PoolWatch.exe is not there), so its record, unins000.dat, goes back at the restart too: until then
# that uninstaller removes nothing, and the Uninstall entry, the logon task and every file stay. The setup says, read
# from the install folder, that PoolWatch does not start until then, and names the file in use with what has it open.
# After the restart's work every file of 1.0.0-beta.1 is back as it was, and no other, and the app folder the setup
# made is gone.
$beta1Files = Get-ProgramFiles
$beta1Dat = Join-Path $app 'unins000.dat'
$holder = Start-Watcher 'upgrade-beta1-failed-held' 'LICENSE.*.old' $exe 'Read'
try {
    $code = Invoke-SetupThatFails 'upgrade-beta1-failed-held'
    $failedLog = Get-UninstallLog 'upgrade-beta1-failed-held'
    Assert-True ($code -ne 0 -and $code -ne 3010) "upgrade-beta1-failed-held: the upgrade fails (exit $code)"
    Assert-True ((Get-Watched 'upgrade-beta1-failed-held') -ieq $exe) "upgrade-beta1-failed-held: the second process held the new app\PoolWatch.exe ($(Get-Watched 'upgrade-beta1-failed-held'))"
    Assert-True ($failedLog -match ([regex]::Escape("Another program has $exe open (in use by ") + "[^\r\n]*process $($holder.Id), run by [^\r\n]*so it stays until the next restart, and no old file goes back before then\.")) 'upgrade-beta1-failed-held: the setup named the new file in use and what has it open'
    Assert-True ($failedLog -match [regex]::Escape("Set aside $beta1Dat until the next restart, when Windows puts it back")) 'upgrade-beta1-failed-held: the setup set 1.0.0-beta.1''s uninstaller record aside until the restart'
    Assert-True ($failedLog -match 'Until that restart PoolWatch does not start: no file that starts it is in place\. Until that restart PoolWatch''s uninstaller removes nothing') 'upgrade-beta1-failed-held: the setup said PoolWatch does not start and its uninstaller removes nothing until the restart'
    Assert-True ($failedLog -match ('names every one\):(\r?\n[^\r\n]*){0,10}?\r?\n[^\r\n]{0,40}?- ' + [regex]::Escape('app\PoolWatch.exe (in use by ') + "[^\r\n]*process $($holder.Id), run by ")) 'upgrade-beta1-failed-held: the setup''s words name the file in use and what has it open, on one of the lines of their list'
    Assert-True ((Test-Path -LiteralPath $exe) -and -not (Test-Path -LiteralPath $launcher) -and -not (Test-Path -LiteralPath $beta1Dat)) 'upgrade-beta1-failed-held: until the restart the install folder has no PoolWatch.exe and no uninstaller record'
    $work = @(Get-RestartWork)
    $back = @($work | Where-Object { $_.To -ne '' })
    $program = @($back | Where-Object { $_.To -notlike "$app\unins*" })
    Assert-True (@($work | Where-Object { $_.To -eq '' -and $_.From -ieq $exe }).Count -eq 1 -and $program.Count -ge 3 -and $program[-1].To -ieq $launcher -and @($back | Where-Object { $_.To -ieq $beta1Dat -and $_.From -like "$beta1Dat.*.old" }).Count -eq 1) "upgrade-beta1-failed-held: Windows deletes the new file and puts every old file back at the next restart, PoolWatch.exe last, and the uninstaller's record ($(@($back | ForEach-Object { $_.To }) -join ', '))"
    # 1.0.0-beta.1's uninstaller, started before that restart as a silent uninstall would start it: it finds no record,
    # stops at its start (Inno Setup's exit code 1, before the step that starts its log) and removes nothing.
    $filesHeld = Get-ProgramFiles
    $p = Start-Process (Get-Uninstaller) -ArgumentList ($silent + "/LOG=`"$Out\uninstall-beta1-before-restart.log`"") -PassThru
    $null = $p.Handle   # keeps the exit code readable after the process ends
    $exited = $p.WaitForExit(120000)
    if (-not $exited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    $filesAfter = Get-ProgramFiles
    $changed = @($filesHeld.Keys | Where-Object { $filesAfter[$_] -ne $filesHeld[$_] }) + @($filesAfter.Keys | Where-Object { -not $filesHeld.ContainsKey($_) })
    Assert-True ($exited -and $p.ExitCode -eq 1 -and -not (Test-Path -LiteralPath "$Out\uninstall-beta1-before-restart.log") -and $changed.Count -eq 0) "upgrade-beta1-failed-held: 1.0.0-beta.1's uninstaller, started before the restart, stopped at its start and removed nothing (exited: $exited, exit $(if ($p.HasExited) { $p.ExitCode } else { 'none' }); $($changed -join ', '))"
    Assert-True ((Get-ItemProperty $uninstallKey -ErrorAction SilentlyContinue).DisplayName -eq 'PoolWatch' -and $null -ne (Get-ScheduledTask -TaskName 'PoolWatch' -TaskPath '\' -ErrorAction SilentlyContinue)) 'upgrade-beta1-failed-held: the Uninstall entry and the logon task are still there'
} finally { Stop-Held $holder }
Write-Host 'CHECK-RAN: upgrade-beta1-failed-held-before-restart'
$notDone = @(Complete-RestartWork)
$filesAfter = Get-ProgramFiles
$changed = @($beta1Files.Keys | Where-Object { $filesAfter[$_] -ne $beta1Files[$_] }) + @($filesAfter.Keys | Where-Object { -not $beta1Files.ContainsKey($_) })
Assert-True ($changed.Count -eq 0) "upgrade-beta1-failed-held: after the restart's work every file of 1.0.0-beta.1 is back as it was, its uninstaller's record included, and no other ($($changed -join ', '))"
Assert-True (-not (Test-Path -LiteralPath (Split-Path $exe))) 'upgrade-beta1-failed-held: after the restart''s work the app folder the setup made is gone'
Assert-True ($notDone.Count -eq 0 -and @(Get-RestartWork).Count -eq 0) "upgrade-beta1-failed-held: the restart's work in the install folder all went through, and Windows has none left ($($notDone -join ', '))"
Write-Host 'CHECK-RAN: upgrade-beta1-failed-held'
# The same failed upgrade while a third process also holds 1.0.0-beta.1's uninstaller record, unins000.dat, without
# letting it be renamed or deleted. The setup then sets that uninstaller's messages, unins000.msg, aside until the
# restart instead: 1.0.0-beta.1's signed uninstaller reads them from that file before it looks for its record, and stops
# without them. Once the record's holder lets go, before the restart, that uninstaller starts, stops at its start
# (exit 1, before the step that starts its log) and removes nothing. Then the same with unins000.msg held too: the
# setup sets that uninstaller's program, unins000.exe, aside instead, so the program the Uninstall entry starts is not
# there until the restart, and says to answer No if Windows offers to remove PoolWatch from its list of apps. Then with
# all three held: the setup can set none aside, and says not to uninstall before the restart. After each restart's
# work every file of 1.0.0-beta.1 is back as it was, and no other.
$beta1Msg = Join-Path $app 'unins000.msg'
$beta1Exe = Join-Path $app 'unins000.exe'
Assert-True (Test-Path -LiteralPath $beta1Msg) '1.0.0-beta.1''s signed uninstaller has its messages in unins000.msg'
Write-Host 'CHECK-RAN: upgrade-beta1-has-msg'
foreach ($step in 'upgrade-beta1-msg-aside', 'upgrade-beta1-exe-aside', 'upgrade-beta1-uninstaller-held') {
    $holder = Start-Watcher $step 'LICENSE.*.old' $exe 'Read'
    $datHolder = Start-Holder $beta1Dat
    $msgHolder = $null
    $exeHolder = $null
    if ($step -ne 'upgrade-beta1-msg-aside') { $msgHolder = Start-Holder $beta1Msg }
    if ($step -eq 'upgrade-beta1-uninstaller-held') { $exeHolder = Start-Holder $beta1Exe }
    try {
        $code = Invoke-SetupThatFails $step
        $failedLog = Get-UninstallLog $step
        Assert-True ($code -ne 0 -and $code -ne 3010) "${step}: the upgrade fails (exit $code)"
        Assert-True ((Get-Watched $step) -ieq $exe) "${step}: the second process held the new app\PoolWatch.exe ($(Get-Watched $step))"
        Write-Host 'CHECK-RAN: upgrade-beta1-held-upgrade-fails'
        $back = @((Get-RestartWork) | Where-Object { $_.To -ne '' })
        if ($step -eq 'upgrade-beta1-msg-aside') {
            Assert-True ($failedLog -match [regex]::Escape("Set aside $beta1Msg until the next restart, when Windows puts it back")) "${step}: the setup set 1.0.0-beta.1's uninstaller messages aside until the restart"
            Assert-True ($failedLog -match 'Until that restart PoolWatch does not start: no file that starts it is in place\. Until that restart PoolWatch''s uninstaller removes nothing') "${step}: the setup said PoolWatch does not start and its uninstaller removes nothing until the restart"
            Assert-True ((Test-Path -LiteralPath $beta1Dat) -and -not (Test-Path -LiteralPath $beta1Msg) -and @($back | Where-Object { $_.To -ieq $beta1Msg -and $_.From -like "$beta1Msg.*.old" }).Count -eq 1) "${step}: until the restart unins000.msg is set aside, and Windows puts it back then"
            Write-Host 'CHECK-RAN: upgrade-beta1-msg-aside-set-aside'
            # The record's holder lets go. 1.0.0-beta.1's uninstaller, started before the restart as a silent uninstall
            # would start it, finds no messages, stops at its start and removes nothing.
            Stop-Held $datHolder
            $datHolder = $null
            $filesHeld = Get-ProgramFiles
            $p = Start-Process (Get-Uninstaller) -ArgumentList ($silent + "/LOG=`"$Out\$step-uninstall.log`"") -PassThru
            $null = $p.Handle   # keeps the exit code readable after the process ends
            $exited = $p.WaitForExit(120000)
            if (-not $exited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
            $filesAfter = Get-ProgramFiles
            $changed = @($filesHeld.Keys | Where-Object { $filesAfter[$_] -ne $filesHeld[$_] }) + @($filesAfter.Keys | Where-Object { -not $filesHeld.ContainsKey($_) })
            Assert-True ($exited -and $p.ExitCode -eq 1 -and -not (Test-Path -LiteralPath "$Out\$step-uninstall.log") -and $changed.Count -eq 0) "${step}: 1.0.0-beta.1's uninstaller, started before the restart, stopped at its start and removed nothing (exited: $exited, exit $(if ($p.HasExited) { $p.ExitCode } else { 'none' }); $($changed -join ', '))"
            Assert-True ((Get-ItemProperty $uninstallKey -ErrorAction SilentlyContinue).DisplayName -eq 'PoolWatch' -and $null -ne (Get-ScheduledTask -TaskName 'PoolWatch' -TaskPath '\' -ErrorAction SilentlyContinue)) "${step}: the Uninstall entry and the logon task are still there"
            Write-Host 'CHECK-RAN: upgrade-beta1-msg-aside'
        }
        elseif ($step -eq 'upgrade-beta1-exe-aside') {
            Assert-True ($failedLog -match [regex]::Escape("Set aside $beta1Exe until the next restart, when Windows puts it back")) "${step}: the setup set 1.0.0-beta.1's uninstaller program aside until the restart"
            Assert-True ($failedLog -match 'Until that restart PoolWatch does not start: no file that starts it is in place\. Until that restart PoolWatch''s uninstaller is not there to start, so it removes nothing: if Windows offers to remove PoolWatch from its list of apps before then, answer No\.') "${step}: the setup said PoolWatch does not start and its uninstaller is not there to start until the restart"
            Assert-True ((Test-Path -LiteralPath $beta1Dat) -and (Test-Path -LiteralPath $beta1Msg) -and -not (Test-Path -LiteralPath $beta1Exe) -and @($back | Where-Object { $_.To -ieq $beta1Exe -and $_.From -like "$beta1Exe.*.old" }).Count -eq 1) "${step}: until the restart unins000.exe is set aside, and Windows puts it back then"
            Write-Host 'CHECK-RAN: upgrade-beta1-exe-aside-set-aside'
            # The record's and the messages file's holders let go: the program the Uninstall entry starts is still
            # not there.
            Stop-Held $datHolder
            $datHolder = $null
            Stop-Held $msgHolder
            $msgHolder = $null
            Assert-True ((Get-Uninstaller) -ieq $beta1Exe -and -not (Test-Path -LiteralPath (Get-Uninstaller))) "${step}: until the restart the Uninstall entry starts nothing ($(Get-Uninstaller))"
            Assert-True ((Get-ItemProperty $uninstallKey -ErrorAction SilentlyContinue).DisplayName -eq 'PoolWatch' -and $null -ne (Get-ScheduledTask -TaskName 'PoolWatch' -TaskPath '\' -ErrorAction SilentlyContinue)) "${step}: the Uninstall entry and the logon task are still there"
            Write-Host 'CHECK-RAN: upgrade-beta1-exe-aside'
        }
        else {
            Assert-True ($failedLog -match [regex]::Escape("None of $beta1Dat, $beta1Msg and $beta1Exe could be set aside until the next restart")) "${step}: the setup could set none of the record, the messages and the program aside"
            Assert-True ($failedLog -match 'Do not uninstall PoolWatch before that restart: the uninstaller of the version you had could not be kept from running until then') "${step}: the setup said not to uninstall before the restart"
            Assert-True ((Test-Path -LiteralPath $beta1Dat) -and (Test-Path -LiteralPath $beta1Msg) -and (Test-Path -LiteralPath $beta1Exe) -and @($back | Where-Object { $_.To -like "$app\unins*" }).Count -eq 0) "${step}: the uninstaller's files stay as they are"
            Write-Host 'CHECK-RAN: upgrade-beta1-uninstaller-held'
        }
    } finally {
        Stop-Held $holder
        if ($datHolder) { Stop-Held $datHolder }
        if ($msgHolder) { Stop-Held $msgHolder }
        if ($exeHolder) { Stop-Held $exeHolder }
    }
    $notDone = @(Complete-RestartWork)
    $filesAfter = Get-ProgramFiles
    $changed = @($beta1Files.Keys | Where-Object { $filesAfter[$_] -ne $beta1Files[$_] }) + @($filesAfter.Keys | Where-Object { -not $beta1Files.ContainsKey($_) })
    Assert-True ($changed.Count -eq 0) "${step}: after the restart's work every file of 1.0.0-beta.1 is back as it was, and no other ($($changed -join ', '))"
    Assert-True (-not (Test-Path -LiteralPath (Split-Path $exe))) "${step}: after the restart's work the app folder the setup made is gone"
    Assert-True ($notDone.Count -eq 0 -and @(Get-RestartWork).Count -eq 0) "${step}: the restart's work in the install folder all went through, and Windows has none left ($($notDone -join ', '))"
    Write-Host 'CHECK-RAN: upgrade-beta1-held-after-restart'
}
# 1.0.0-beta.1 running, started as that task starts it.
$old = Start-Running 'tray-beta1'
Install 'upgrade-beta1'
$upgradeLog = Get-UninstallLog 'upgrade-beta1'
Assert-True ($upgradeLog -match 'The installed PoolWatch, made before the launcher, was asked to exit through its exit signal\.') 'the setup asked 1.0.0-beta.1 to exit through that version''s own exit signal'
Assert-True ($upgradeLog -notmatch 'answered --stop-running|could not be started to ask it to exit') 'the setup started no program of 1.0.0-beta.1 to stop it'
Write-Host 'CHECK-RAN: upgrade-beta1-stopped-by-signal'
Assert-StoppedBySignal $old 'upgrade from 1.0.0-beta.1'
Assert-True ((Test-Path $exe) -and (Test-Path (Join-Path $app 'app\amd64\KernelTraceControl.dll'))) 'the upgrade put PoolWatch itself, with its DLLs, below the launcher'
$stale = @('amd64', 'PoolWatch-selfcontained.exe', 'PoolWatchLauncher.exe') | Where-Object { Test-Path (Join-Path $app $_) }
Assert-True ($stale.Count -eq 0) "the upgrade left nothing of the layout before the launcher ($($stale -join ', '))"
$aside = @(Get-ChildItem -LiteralPath $app -Recurse -Force -Filter '*.old' -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
Assert-True ($aside.Count -eq 0) "the upgrade from 1.0.0-beta.1 left no old file set aside ($($aside -join ', '))"
Assert-True (@(Get-RestartWork).Count -eq 0) 'the upgrade from 1.0.0-beta.1 left Windows no work in the install folder'
Assert-True ((Test-Path -LiteralPath $launcher) -and (Test-Path -LiteralPath $exe) -and (Get-Item $launcher).VersionInfo.ProductVersion -eq (Get-Item $exe).VersionInfo.ProductVersion) "the install folder's PoolWatch.exe is now the new launcher"
$task = Get-ScheduledTask -TaskName 'PoolWatch' -TaskPath '\' -ErrorAction SilentlyContinue
$actions = @($task.Actions)
Assert-True ($actions.Count -eq 1 -and $actions[0].Execute -eq $launcher) "1.0.0-beta.1's logon task is as it was, and starts the launcher ($($actions.Execute))"
# What that task now starts: the launcher, which starts PoolWatch itself below it.
$pw = Start-Running 'tray-0'
$program = @(Get-CimInstance Win32_Process -Filter "Name = 'PoolWatch.exe'" | Where-Object { $_.ExecutablePath -ieq $exe })
Assert-True ($program.Count -ge 1) 'the task''s program, the launcher, started PoolWatch itself (app\PoolWatch.exe)'
Uninstall 'uninstall-0'
Assert-StoppedBySignal $pw 'uninstaller'
Assert-True (-not (Test-Path $app)) 'the install folder is gone'
if (Test-Path $app) { Show-InstallFolder 'uninstall-0' }
Assert-True ($null -eq (Get-ScheduledTask -TaskName 'PoolWatch' -TaskPath '\' -ErrorAction SilentlyContinue)) 'the logon task is gone'

Write-Host '--- Pass 0b: the setup over a copy of 1.0.0-beta.1''s self-contained zip, then over itself while the launcher runs under that zip''s name'
# 1.0.0-beta.1's self-contained zip, pinned by the SHA-256 its release lists in SHA256SUMS.txt, unpacked by hand into
# the install folder as that version's user guide said: PoolWatch itself is PoolWatch-selfcontained.exe there, the name
# a logon task or a shortcut that copy made starts.
$oldZip = Join-Path $Out 'PoolWatch-v1.0.0-beta.1-win-x64-selfcontained.zip'
Invoke-WebRequest -Uri 'https://github.com/nubbymong/poolwatch/releases/download/v1.0.0-beta.1/PoolWatch-v1.0.0-beta.1-win-x64-selfcontained.zip' -OutFile $oldZip
$zipHash = (Get-FileHash -LiteralPath $oldZip -Algorithm SHA256).Hash
if ($zipHash -ne '0FB6DCCBF9BF7BB49EF77F3D89B6B04268F7F04F3E624CCC519E26FBD2514510') { throw "the 1.0.0-beta.1 self-contained zip's SHA-256 is $zipHash, not the one its release lists" }
Expand-Archive -LiteralPath $oldZip -DestinationPath $app
$second = Join-Path $app 'PoolWatch-selfcontained.exe'
Assert-True ((Test-Path $second) -and -not (Test-Path $launcher) -and -not (Test-Path $exe)) '1.0.0-beta.1''s self-contained zip has PoolWatch itself as PoolWatch-selfcontained.exe'
$oldVersion = (Get-Item $second -ErrorAction SilentlyContinue).VersionInfo.ProductVersion
# The setup keeps that name and puts the launcher there too, so such a task or shortcut keeps starting PoolWatch.
Install 'upgrade-beta1-sc'
foreach ($f in $launcher, $second) {
    Assert-True ((Test-Path $f) -and (Test-Path -LiteralPath $exe) -and (Get-Item $f).VersionInfo.ProductVersion -eq (Get-Item $exe).VersionInfo.ProductVersion -and
        (Get-Item $f).VersionInfo.ProductVersion -ne $oldVersion) "the setup put the launcher at $f"
}
Assert-True (-not (Test-Path (Join-Path $app 'amd64'))) 'the setup over the self-contained zip left nothing of the layout before the launcher'
# PoolWatch started under that name, as such a task or shortcut starts it, then the setup again: its stop and its wait
# know the launcher by that name too, so it stops that PoolWatch through its exit signal and puts the files in place now
# (Install fails on the exit code 3010 of a setup that finishes at the next restart).
$sc = Start-Running 'tray-0b' $second
Install 'reinstall-0b'
Assert-StoppedBySignal $sc 'setup over the launcher''s second name'
Assert-True (@(Get-RestartWork).Count -eq 0) 'the setup over the launcher''s second name left Windows no work in the install folder'
Write-Host 'CHECK-RAN: reinstall-0b'
Uninstall 'uninstall-0b'
Assert-True (-not (Test-Path $app)) 'the install folder is gone after Pass 0b'
if (Test-Path $app) { Show-InstallFolder 'uninstall-0b' }

Write-Host '--- Pass 1: install, change, uninstall while PoolWatch runs'
Install 'install-1'
# The pages' code ran, at the runner's display scale: each page's text ends inside the page, the Ready page's parts are
# in order (steps, then the panel, then "Click Install to continue."), and every icon on it could be shown.
$installLog = "$(Get-Content "$Out\install-1.log" -Raw -ErrorAction SilentlyContinue)"
$welcome = [regex]::Match($installLog, $welcomeLine)
Assert-True ($welcome.Success -and [int]$welcome.Groups[3].Value -le [int]$welcome.Groups[2].Value) "the Welcome page's text fits on the page ($($welcome.Value))"
Write-Host 'CHECK-RAN: installer-welcome-page-fits'
$ready = [regex]::Match($installLog, $readyLine)
$fits = $ready.Success -and [int]$ready.Groups[3].Value -lt [int]$ready.Groups[4].Value -and
    [int]$ready.Groups[5].Value -le [int]$ready.Groups[6].Value -and [int]$ready.Groups[7].Value -le [int]$ready.Groups[2].Value
Assert-True $fits "the Ready page's steps, panel and last line are in order and fit on the page ($($ready.Value))"
Write-Host 'CHECK-RAN: installer-ready-page-fits'
Assert-True ($installLog -notmatch "The Ready page's icon \S+ could not be shown") 'the setup showed every icon on its Ready page'
Write-Host 'CHECK-RAN: installer-every-icon-shown'
foreach ($f in 'PoolWatch.exe', 'app\PoolWatch.exe', 'unins000.exe', 'chatgpt-no-shader-cache.ps1', 'AGENTS.md', 'LICENSE',
               'THIRD-PARTY-NOTICES.md', 'app\amd64\KernelTraceControl.dll', 'licenses\Microsoft.NETCore.App\LICENSE.TXT',
               'licenses\Microsoft.DotNet.ILCompiler\LICENSE.TXT', 'licenses\Microsoft.DotNet.ILCompiler\THIRD-PARTY-NOTICES.TXT') {
    Assert-True (Test-Path (Join-Path $app $f)) "installed $f"
    Write-Host 'CHECK-RAN: installer-installed-each-file'
}
foreach ($f in 'PoolWatch-selfcontained.exe', 'PoolWatchLauncher.exe', 'amd64') {
    Assert-True (-not (Test-Path (Join-Path $app $f))) "no $f in the install folder"
}
# The two programs are what the build made: the launcher (native, its own manifest asks for administrator rights) and
# PoolWatch itself below it, of the same version.
Assert-True ((Test-Path -LiteralPath $launcher) -and (Test-Path -LiteralPath $exe) -and (Get-Item $launcher).VersionInfo.ProductVersion -eq (Get-Item $exe).VersionInfo.ProductVersion) 'the launcher and PoolWatch itself are of the same version'
Assert-True ((Test-Path -LiteralPath $launcher) -and [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($launcher)).IndexOf('level="requireAdministrator"') -ge 0) "the install folder's PoolWatch.exe is the launcher (it asks for administrator rights)"
Assert-True ((Test-Path -LiteralPath $exe) -and [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($exe)).IndexOf('level="asInvoker"') -ge 0) 'app\PoolWatch.exe is PoolWatch itself (it asks for none)'
Assert-True (Test-Path $startMenu) 'Start menu entry'
$target = (New-Object -ComObject WScript.Shell).CreateShortcut($startMenu).TargetPath
Assert-True ($target -eq $launcher) "the Start menu entry starts the launcher ($target)"
$entry = Get-ItemProperty $uninstallKey -ErrorAction SilentlyContinue
Assert-True ($entry.DisplayName -eq 'PoolWatch') "Uninstall entry ($($entry.DisplayName) $($entry.DisplayVersion))"
# Apps and Features starts the uninstaller with /LOG, so it writes a log then (UninstallLogging; without /LOG it writes
# none).
Assert-True ("$($entry.UninstallString)" -eq "`"$(Join-Path $app 'unins000.exe')`" /LOG") "the Uninstall entry starts the uninstaller with /LOG ($($entry.UninstallString))"
Write-Host 'CHECK-RAN: installer-uninstall-entry-logs'

$code = Invoke-Installed 'selftest' @('--selftest', '--out', "$Out\selftest")
Assert-True ($code -eq 0) "the installed PoolWatch runs from its install folder (selftest exit $code)"
$code = Invoke-Installed 'service-off-1' @('--service-off', $service)
Assert-True ($code -eq 0 -and (Get-StartMode $service) -eq 'Disabled') "the installed PoolWatch turned $service off (exit $code)"
# PoolWatch itself (app\PoolWatch.exe), started by an administrator (this runner): it starts again through the launcher,
# waits for it, and passes on its output and its exit code.
$code = Invoke-Installed 'changes-dump' @('--changes-dump', "$Out\changes.json", '--out', $Out) -program $exe
$changes = @(Get-Content "$Out\changes.json" -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json)
Assert-True ($code -eq 0 -and @($changes | Where-Object { $_.id -eq "service:$($service.ToLowerInvariant())" }).Count -eq 1) "--changes-dump lists $service (exit $code)"
Assert-True ("$(Get-Content "$Out\changes-dump.txt" -Raw -ErrorAction SilentlyContinue)" -match 'changes written: ') 'app\PoolWatch.exe started directly passed on the output of the PoolWatch its launcher started'
# A logon task like the one Settings registers (it starts the launcher with --tray, for this account signed in
# interactively, with highest privileges, so PoolWatch's check at start finds its own task and asks nothing): the
# uninstaller must remove it.
$taskPrincipal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Highest
Register-ScheduledTask -TaskName 'PoolWatch' -TaskPath '\' -Action (New-ScheduledTaskAction -Execute $launcher -Argument '--tray') `
    -Trigger (New-ScheduledTaskTrigger -AtLogOn) -Principal $taskPrincipal -Force | Out-Null

$pw = Start-Running 'tray-1'
Uninstall 'uninstall-1'
Assert-StoppedBySignal $pw 'uninstaller'
Assert-HelperInJob $pw 'tray-1'
Write-Host 'CHECK-RAN: installer-checked-after-uninstall'
Assert-True ((Get-StartMode $service) -eq $before) "the uninstaller put $service back to $before (got $(Get-StartMode $service))"
Assert-True ($null -eq (Get-ScheduledTask -TaskName 'PoolWatch' -TaskPath '\' -ErrorAction SilentlyContinue)) 'the logon task is gone'
Assert-True (-not (Test-Path $app)) 'the install folder is gone'
if (Test-Path $app) { Show-InstallFolder 'uninstall-1' }
Assert-True (-not (Test-Path $startMenu)) 'the Start menu entry is gone'
Assert-True (-not (Test-Path $uninstallKey)) 'the Uninstall entry is gone'
$summary = Get-Content (Join-Path $data 'uninstall-summary.txt') -Raw -ErrorAction SilentlyContinue
Write-Host $summary
# This build turned the service off itself, seeing whether it was running then, so the uninstaller is expected to put it
# back whole: a "Put back:" line. A "Partly put back:" line (its start type back, but not started again) fails this check.
Assert-True ($summary -match "(?m)^Put back: .*\b$([regex]::Escape($service))\b") "uninstall-summary.txt lists $service as put back"
Write-Host 'CHECK-RAN: installer-summary-lists-service'
Assert-True (Test-Path $data) 'the data folder is kept'
$uninstallLog = Get-UninstallLog 'uninstall-1'
Assert-True ($uninstallLog -notmatch 'could not list its changes') 'the uninstaller read the list of changes from the installed PoolWatch'
Assert-True ($uninstallLog -match "Uninstalling puts back: Turned off .*\b$([regex]::Escape($service))\b") "the uninstaller listed $service as one it puts back"
Assert-True ($uninstallLog -match 'Uninstalling puts back: PoolWatch starts when you sign in') 'the uninstaller listed the logon task as one it puts back'
Assert-True ($uninstallLog -match 'Every change is to be put back \(one PoolWatch cannot put back stays\)\.') 'a silent uninstall chose to put the changes back'
Assert-True (-not (Test-Path (Join-Path $data 'uninstall-list.json'))) 'the uninstaller deleted the list it read'
Write-Host 'CHECK-RAN: uninstall-1-choice'

Write-Host '--- Pass 2: an upgrade while PoolWatch runs, then an uninstall with /KEEPCHANGES'
Install 'install-2'
$pw = Start-Running 'tray-2'
# A logon task as earlier versions registered it (the install folder's PoolWatch.exe --tray, for this account at its
# sign-in), turned off in Task Scheduler: the upgrade leaves it exactly as it was, for the same account and sign-in,
# still off, and it starts the launcher.
$account = [Security.Principal.WindowsIdentity]::GetCurrent().Name
Register-ScheduledTask -TaskName 'PoolWatch' -TaskPath '\' -Action (New-ScheduledTaskAction -Execute $launcher -Argument '--tray') `
    -Trigger (New-ScheduledTaskTrigger -AtLogOn -User $account) `
    -Principal (New-ScheduledTaskPrincipal -UserId $account -LogonType Interactive -RunLevel Highest) -Force | Out-Null
Disable-ScheduledTask -TaskName 'PoolWatch' -TaskPath '\' | Out-Null
Install 'upgrade-2'
Assert-StoppedBySignal $pw 'upgrade'
Assert-HelperInJob $pw 'tray-2'
Write-Host 'CHECK-RAN: installer-checked-after-upgrade'
Assert-True ((Test-Path $exe) -and (Test-Path (Join-Path $app 'unins000.exe')) -and (Test-Path $uninstallKey)) 'the upgrade left PoolWatch installed'
$task = Get-ScheduledTask -TaskName 'PoolWatch' -TaskPath '\' -ErrorAction SilentlyContinue
$actions = @($task.Actions)
Assert-True ($actions.Count -eq 1 -and $actions[0].Execute -eq $launcher -and $actions[0].Arguments -eq '--tray') "the logon task still starts the launcher ($($actions.Execute) $($actions.Arguments))"
# Task Scheduler may give an account by name, with or without its computer's name, or by SID: compared by SID.
function Get-Sid([string]$name) { try { (New-Object Security.Principal.NTAccount $name).Translate([Security.Principal.SecurityIdentifier]).Value } catch { $name } }
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$runsAs = "$($task.Principal.UserId)"
$signIn = "$(@($task.Triggers)[0].UserId)"
Assert-True (((Get-Sid $runsAs) -eq $sid) -and ((Get-Sid $signIn) -eq $sid)) "the logon task stays this account's (runs as $runsAs, at the sign-in of $signIn)"
Assert-True ("$($task.State)" -eq 'Disabled') "the logon task stays turned off ($($task.State))"
# An upgrade that replaces the files in place renames every old file aside before it writes any new one, and deletes
# them once every new one is in place: none is left, and none is left for Windows to do at the next restart.
$aside = @(Get-ChildItem -LiteralPath $app -Recurse -Force -Filter '*.old' -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
Assert-True ($aside.Count -eq 0) "the upgrade left no old file set aside ($($aside -join ', '))"
Assert-True (@(Get-RestartWork).Count -eq 0) 'the upgrade left Windows no work in the install folder'

# The setup sees a process started through the short (8.3) spelling of the install folder (or a mix of short and long
# parts, where the folder itself has no short name). The setup reads each process's exe path from the kernel
# (QueryFullProcessImageNameW), which gives a path even for a process that has not run yet (started suspended) and keeps
# the spelling it was started through; the setup compares the two paths in their short form, so it must name this one by
# its path. The exe is in use, so the setup finishes at the next restart (Assert-FinishesAtRestart). A token-leak test
# helper started so is ended, and the upgrade goes on in place.
$short = [HeldExe]::ShortPath($exe)
if ($short -eq $exe) {
    Write-Host "  the install folder has no short spelling on this runner ($exe), so a process started through one is not checked"
} else {
    Write-Host "  the installed exe's short spelling: $short"
    $held = [HeldExe]::StartSuspended($short, '--tray')
    try { Assert-FinishesAtRestart 'upgrade-held' $held } finally { Stop-Held $held }
    $helper = [HeldExe]::StartSuspended($short, "--token-leak-test 1 `"$Out\stray-helper.json`"")
    try {
        $skip = @(Get-Content $log -ErrorAction SilentlyContinue).Count
        Install 'upgrade-helper'
        $byPoolWatch = @(Get-Content $log -ErrorAction SilentlyContinue | Select-Object -Skip $skip) -match "ended the token-leak test helper \(process $($helper.Id)\)"
        $bySetup = (Get-UninstallLog 'upgrade-helper') -match "Ended the token-leak test helper \(process $($helper.Id)\)"
        Assert-True (($byPoolWatch.Count -gt 0 -or $bySetup) -and $helper.HasExited) "a token-leak test helper started through the short spelling was ended and did not hold the upgrade (process $($helper.Id))"
    } finally { Stop-Held $helper }
}
# The same through a junction that points at the install folder: the kernel gives the exe's real path for a process
# started through a junction (not the path the process was started with, which WMI would give), so the setup names it
# by that path, and finishes at the next restart. The junction itself is removed afterwards, never what it points at.
$junction = Join-Path $Out 'junction-to-install-folder'
New-Item -ItemType Junction -Path $junction -Target $app | Out-Null
$held = [HeldExe]::StartSuspended((Join-Path $junction 'app\PoolWatch.exe'), '--tray')
try { Assert-FinishesAtRestart 'upgrade-junction' $held }
finally {
    Stop-Held $held
    [System.IO.Directory]::Delete($junction)
}
# A second process holds a file of PoolWatch's (one of its native DLLs) open without letting it be renamed or deleted, as
# another account's program could: the setup finishes at the next restart, names the file and that process, and asks for
# no restart (it is not told /NORESTART). Then the setup again before that restart: Inno Setup's own check for an
# unfinished earlier install must find none of this run's files in Windows' list (it would otherwise restart the
# runner), and this run finishes at the restart too, its moves after the first run's. Then the holder lets go and this
# test does the restart's work: the second run's files are in place, and nothing of either run's own is left.
$dll = Join-Path $app 'app\amd64\KernelTraceControl.dll'
$holder = Start-Holder $dll
$hash = ''
try {
    foreach ($name in 'upgrade-file-held', 'upgrade-file-held-again') {
        $code = Invoke-Setup $name
        $setupLog = Get-UninstallLog $name
        Assert-True ($code -eq 3010) "${name}: the setup finishes at the next restart (exit 3010, got $code)"
        Assert-True ($setupLog -match 'Need to restart Windows\? No') "${name}: the setup asked for no restart"
        Assert-True ($setupLog -notmatch 'Found pending rename or delete that matches one of our files') "${name}: Inno Setup's own check found none of the setup's files in Windows' list"
        Assert-True ($setupLog -match ([regex]::Escape("In use, so it cannot be replaced or removed now: $dll (in use by ") + "[^\r\n]*process $($holder.Id), run by ")) "${name}: the setup named the file in use and the process that has it open (process $($holder.Id))"
        Assert-True ($setupLog -match 'Windows puts this version of PoolWatch in place the next time it restarts') "${name}: the setup says Windows puts this version in place at the next restart"
        Write-Host 'CHECK-RAN: upgrade-file-held'
    }
    Assert-True ((Get-UninstallLog 'upgrade-file-held-again') -match 'An earlier setup or uninstall of PoolWatch finishes when Windows restarts, so this one does too, after it\.') 'the setup run again before the restart finishes at the restart too, after the first'
    $moves = @((Get-RestartWork) | Where-Object { $_.To -ieq $exe })
    Assert-True ($moves.Count -eq 2 -and [string]::CompareOrdinal($moves[1].From, $moves[0].From) -gt 0) "both runs' moves of app\PoolWatch.exe are in Windows' list, the second run's last ($(@($moves | ForEach-Object { $_.From }) -join ', '))"
    if ($moves.Count -gt 0 -and (Test-Path -LiteralPath $moves[-1].From)) { $hash = (Get-FileHash -LiteralPath $moves[-1].From).Hash }
} finally { Stop-Held $holder }
Write-Host 'CHECK-RAN: upgrade-file-held-moves'
$notDone = @(Complete-RestartWork)
Assert-True ($hash -and (Test-Path -LiteralPath $exe) -and (Get-FileHash -LiteralPath $exe).Hash -eq $hash) 'after the restart''s work the second run''s app\PoolWatch.exe is in place'
foreach ($f in $launcher, $exe, $dll, (Get-Uninstaller)) { Assert-True (Test-Path -LiteralPath $f) "after the restart's work $f is there" }
Assert-True (-not (Test-Path -LiteralPath (Join-Path $app 'at-restart'))) 'after the restart''s work no folder of the setups'' own is left'
Assert-True ($notDone.Count -eq 0 -and @(Get-RestartWork).Count -eq 0) "both runs' restart work in the install folder all went through, and Windows has none left ($($notDone -join ', '))"
Write-Host 'CHECK-RAN: upgrade-file-held-after-restart'

# A setup with nothing in use puts its uninstaller back in the install folder itself, and the one an earlier setup wrote
# in a folder of its own goes.
Install 'reinstall-2'
Assert-True ((Get-Uninstaller) -ieq (Join-Path $app 'unins000.exe') -and (Test-Path -LiteralPath (Join-Path $app 'unins000.exe'))) "a setup with nothing in use writes its uninstaller in the install folder ($(Get-Uninstaller))"
Assert-True (-not (Test-Path -LiteralPath (Join-Path $app 'uninstall'))) 'the uninstaller an earlier setup wrote in a folder of its own is gone'

# A second process holds the uninstaller's own record, unins000.dat, as any account that can read the install folder
# could: the setup still replaces PoolWatch's files now (exit 0), and writes its uninstaller in a folder of its own, which
# the Uninstall entry then names; the held file stays until a later setup or the uninstall can delete it.
$dat = Join-Path $app 'unins000.dat'
$holder = Start-Holder $dat
try {
    $code = Invoke-Setup 'upgrade-uninstaller-held'
    $setupLog = Get-UninstallLog 'upgrade-uninstaller-held'
    Assert-True ($code -eq 0) "a setup while the uninstaller's record is held replaces the files now (exit $code)"
    Assert-True ($setupLog -match [regex]::Escape("In use, so Setup writes its uninstaller in a folder of its own: $dat")) 'the setup named the uninstaller file in use'
    $uninstaller = Get-Uninstaller
    Assert-True ($uninstaller -like "$app\uninstall\*\unins000.exe" -and (Test-Path -LiteralPath $uninstaller)) "the Uninstall entry names the uninstaller in a folder of the setup's own ($uninstaller)"
    Assert-True (@(Get-RestartWork).Count -eq 0) 'the setup while the uninstaller''s record is held left Windows no work in the install folder'
} finally { Stop-Held $holder }
Write-Host 'CHECK-RAN: upgrade-uninstaller-held'
$code = Invoke-Installed 'service-off-2' @('--service-off', $service)
Assert-True ($code -eq 0 -and (Get-StartMode $service) -eq 'Disabled') "the installed PoolWatch turned $service off again (exit $code)"
Uninstall 'uninstall-2' @('/KEEPCHANGES')
Assert-True ((Get-StartMode $service) -eq 'Disabled') "/KEEPCHANGES left $service off"
$saved = @(Get-Content (Join-Path $data 'service-changes.json') -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json)
Assert-True (@($saved | Where-Object { $_.service -eq $service }).Count -eq 1) "/KEEPCHANGES kept the record of $service"
$summary = Get-Content (Join-Path $data 'uninstall-summary.txt') -Raw -ErrorAction SilentlyContinue
Write-Host $summary
Assert-True ($summary -match "(?m)^Kept: .*\b$([regex]::Escape($service))\b") "uninstall-summary.txt lists $service as kept"
Assert-True ((Get-UninstallLog 'uninstall-2') -match 'The changes are to be kept: only the logon task and the desktop shortcut are to go\.') 'the /KEEPCHANGES uninstall chose to keep the changes'
Assert-True ($null -eq (Get-ScheduledTask -TaskName 'PoolWatch' -TaskPath '\' -ErrorAction SilentlyContinue)) '/KEEPCHANGES still removed the logon task'
Assert-True (-not (Test-Path $app)) 'the install folder is gone'
Write-Host 'CHECK-RAN: uninstall-2-choice'
if (Test-Path $app) { Show-InstallFolder 'uninstall-2' }


Write-Host '--- Last: a reinstall, a failed upgrade, then a silent /DELETEDATA uninstall puts the kept change back by itself and removes the data folder'
Install 'install-3'
Assert-True ((Get-StartMode $service) -eq 'Disabled') "$service is still off, as /KEEPCHANGES left it"
Write-Host 'CHECK-RAN: reinstall-change-still-off'
Assert-HelperInJobFromTask 'task-3'
Assert-HelperEndsWithKill 'tray-3'

# An upgrade that fails after every file was written, here because its Uninstall entry cannot be written (a deny rule for
# administrators on that key, for this step only, which keeps Inno Setup from writing or deleting it): Inno Setup rolls
# back, deleting the new files and the Start menu entry, and the setup puts every old file back, the uninstaller's
# program it had replaced (Inno Setup replaces it before it writes the entry) and the Start menu entry, so the version
# that was there stays whole. Two of its files are marked first, so that an old file can be told from a new one of the
# same build. Every file of the install folder is compared, the uninstaller's own among them.
foreach ($f in (Join-Path $app 'LICENSE'), (Join-Path $app 'AGENTS.md')) { Add-Content -LiteralPath $f -Value 'PoolWatch installer test: the file that was there' }
$filesBefore = Get-ProgramFiles
$code = Invoke-SetupThatFails 'upgrade-failed'
Assert-True ($code -ne 0 -and $code -ne 3010) "an upgrade whose Uninstall entry cannot be written fails (exit $code)"
$filesAfter = Get-ProgramFiles
$changed = @($filesBefore.Keys | Where-Object { $filesAfter[$_] -ne $filesBefore[$_] }) + @($filesAfter.Keys | Where-Object { -not $filesBefore.ContainsKey($_) })
Assert-True ($changed.Count -eq 0) "the failed upgrade left every file of the version that was there as it was, and no other ($($changed -join ', '))"
Write-Host 'CHECK-RAN: upgrade-failed-files-as-they-were'
$failedLog = Get-UninstallLog 'upgrade-failed'
Assert-True ($failedLog -match [regex]::Escape("Put back $exe.")) 'the failed upgrade put the old app\PoolWatch.exe back'
Assert-True ($failedLog -match [regex]::Escape("Put back $(Join-Path $app 'unins000.exe') as it was before the setup.")) 'the failed upgrade put back the uninstaller''s program it had replaced'
Assert-True (Test-Path $startMenu) 'the failed upgrade left the Start menu entry'
Assert-True ((Get-ItemProperty $uninstallKey -ErrorAction SilentlyContinue).DisplayName -eq 'PoolWatch' -and (Test-Path -LiteralPath (Get-Uninstaller))) 'the failed upgrade left the Uninstall entry and its uninstaller'
Assert-True (@(Get-RestartWork).Count -eq 0) 'the failed upgrade left Windows no work in the install folder'
Write-Host 'CHECK-RAN: upgrade-failed'

# The same failed upgrade while a second process holds one of the NEW files open without letting it be renamed or
# deleted (it opens the new LICENSE as soon as the setup has set the old one aside and written the new one): the
# rollback cannot delete that file, nor the setup move it out of the way, so it stays until the next restart, and so
# every old file goes back only then, over it, the launcher last. Until then the install folder has no launcher and no
# PoolWatch itself, so nothing starts PoolWatch from old and new files mixed, and the setup says so. This version's
# uninstaller, started before the restart, starts no cleanup and removes nothing, and says to restart Windows first.
# Then the holder lets go and this test does the restart's work: every file of the version that was there is back as
# it was.
$license = Join-Path $app 'LICENSE'
$filesBefore = Get-ProgramFiles
$holder = Start-Watcher 'upgrade-failed-held' 'LICENSE.*.old' $license 'Read'
try {
    $code = Invoke-SetupThatFails 'upgrade-failed-held'
    $failedLog = Get-UninstallLog 'upgrade-failed-held'
    Assert-True ($code -ne 0 -and $code -ne 3010) "upgrade-failed-held: the upgrade fails (exit $code)"
    Assert-True ((Get-Watched 'upgrade-failed-held') -ieq $license) "upgrade-failed-held: the second process held the new LICENSE ($(Get-Watched 'upgrade-failed-held'))"
    Assert-True ($failedLog -match ([regex]::Escape("Another program has $license open (in use by ") + "[^\r\n]*process $($holder.Id), run by [^\r\n]*so it stays until the next restart, and no old file goes back before then\.")) 'upgrade-failed-held: the setup named the new file in use and what has it open'
    Assert-True ((Test-Path -LiteralPath $license) -and -not (Test-Path -LiteralPath $launcher) -and -not (Test-Path -LiteralPath $exe)) 'upgrade-failed-held: until the restart the install folder has no launcher and no PoolWatch itself'
    Assert-True ($failedLog -match 'Until that restart PoolWatch does not start: no file that starts it is in place\. Until that restart PoolWatch''s uninstaller removes nothing') 'upgrade-failed-held: the setup said PoolWatch does not start and its uninstaller removes nothing until the restart'
    $back = @((Get-RestartWork) | Where-Object { $_.To -ne '' })
    Assert-True ($back.Count -ge 3 -and $back[-1].To -ieq $launcher -and @($back | Where-Object { $_.To -ieq $license -and $_.From -like "$license.*.old" }).Count -eq 1) "upgrade-failed-held: Windows puts every old file back at the next restart, LICENSE over the new one, the launcher last ($(@($back | ForEach-Object { $_.To }) -join ', '))"
    # This version's uninstaller, started before that restart as a silent uninstall would start it: the install folder
    # has no launcher of its program's version, so it starts no cleanup, removes nothing and says to restart first.
    $filesHeld = Get-ProgramFiles
    $p = Start-Process (Get-Uninstaller) -ArgumentList ($silent + "/LOG=`"$Out\uninstall-failed-held-before-restart.log`"") -Wait -PassThru
    $deadline = (Get-Date).AddMinutes(2)
    while ((Get-CimInstance Win32_Process -Filter "Name LIKE '[_]unins%'") -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
    $filesAfter = Get-ProgramFiles
    $changed = @($filesHeld.Keys | Where-Object { $filesAfter[$_] -ne $filesHeld[$_] }) + @($filesAfter.Keys | Where-Object { -not $filesHeld.ContainsKey($_) })
    Assert-True ($changed.Count -eq 0 -and (Get-UninstallLog 'uninstall-failed-held-before-restart') -match 'finishes when Windows next restarts, so its files are not in place yet: PoolWatch put nothing back and the uninstaller has removed nothing') "upgrade-failed-held: this version's uninstaller, started before the restart, removed nothing and said to restart Windows first (exit $($p.ExitCode); $($changed -join ', '))"
    Assert-True ((Test-Path $startMenu) -and (Get-ItemProperty $uninstallKey -ErrorAction SilentlyContinue).DisplayName -eq 'PoolWatch' -and (Get-StartMode $service) -eq 'Disabled') "upgrade-failed-held: after that uninstall the Start menu entry, the Uninstall entry and the change to $service are as they were"
} finally { Stop-Held $holder }
Write-Host 'CHECK-RAN: upgrade-failed-held-before-restart'
$notDone = @(Complete-RestartWork)
$filesAfter = Get-ProgramFiles
$changed = @($filesBefore.Keys | Where-Object { $filesAfter[$_] -ne $filesBefore[$_] }) + @($filesAfter.Keys | Where-Object { -not $filesBefore.ContainsKey($_) })
Assert-True ($changed.Count -eq 0) "upgrade-failed-held: after the restart's work every file of the version that was there is back as it was, and no other ($($changed -join ', '))"
Assert-True ((Test-Path $startMenu) -and (Get-ItemProperty $uninstallKey -ErrorAction SilentlyContinue).DisplayName -eq 'PoolWatch') 'upgrade-failed-held: the Start menu entry and the Uninstall entry are there'
Assert-True ($notDone.Count -eq 0 -and @(Get-RestartWork).Count -eq 0) "upgrade-failed-held: the restart's work in the install folder all went through, and Windows has none left ($($notDone -join ', '))"
Write-Host 'CHECK-RAN: upgrade-failed-held'

# An upgrade that fails later, after Inno Setup has written the new Uninstall entry: a second process opens the
# uninstaller's record, unins000.dat, once the setup has looked at it and begun setting the old files aside, so Inno
# Setup cannot save the record at its end. Its rollback deletes the new entry (Inno Setup deleted the old one when it
# wrote the new one), and the setup writes the old entry back as it was, with every old file and the uninstaller's
# program it had replaced.
$dat = Join-Path $app 'unins000.dat'
$filesBefore = Get-ProgramFiles
$entryBefore = Get-Entry
$holder = Start-Watcher 'upgrade-failed-late' '*.old' $dat 'ReadWrite'
try {
    $code = Invoke-Setup 'upgrade-failed-late'
    $failedLog = Get-UninstallLog 'upgrade-failed-late'
    Assert-True ($code -ne 0 -and $code -ne 3010) "upgrade-failed-late: an upgrade that cannot save the uninstaller's record fails (exit $code)"
    Assert-True ((Get-Watched 'upgrade-failed-late') -ieq $dat) "upgrade-failed-late: the second process held $dat ($(Get-Watched 'upgrade-failed-late'))"
    Assert-True ($failedLog -match 'Put back the Apps and Features entry as it was before the setup\.') 'upgrade-failed-late: the setup wrote the Uninstall entry back'
} finally { Stop-Held $holder }
Write-Host 'CHECK-RAN: upgrade-failed-late-entry-put-back'
$entryAfter = Get-Entry
$entryChanged = @($entryBefore.Keys | Where-Object { $entryAfter[$_] -ne $entryBefore[$_] }) + @($entryAfter.Keys | Where-Object { -not $entryBefore.ContainsKey($_) })
Assert-True ($entryBefore.Count -gt 0 -and $entryChanged.Count -eq 0) "upgrade-failed-late: the Uninstall entry is as it was, every value ($($entryChanged -join ', '))"
Write-Host 'CHECK-RAN: upgrade-failed-late-entry-as-it-was'
$filesAfter = Get-ProgramFiles
$changed = @($filesBefore.Keys | Where-Object { $filesAfter[$_] -ne $filesBefore[$_] }) + @($filesAfter.Keys | Where-Object { -not $filesBefore.ContainsKey($_) })
Assert-True ($changed.Count -eq 0) "upgrade-failed-late: every file of the version that was there is as it was, and no other ($($changed -join ', '))"
Assert-True ((Test-Path $startMenu) -and (Test-Path -LiteralPath (Get-Uninstaller))) 'upgrade-failed-late: the Start menu entry and the uninstaller the entry names are there'
Assert-True (@(Get-RestartWork).Count -eq 0) 'upgrade-failed-late: Windows has no work left in the install folder'
Write-Host 'CHECK-RAN: upgrade-failed-late'

# An upgrade during which a file comes into use while the setup sets the old files aside: a second process opens the
# first old file set aside, under its new name, and shares it with no one, so it can be neither renamed nor read. The
# setup looks at each again (for a few seconds, as for a file open only for a moment), puts every one back before
# writing any new file (that one under a second name of the same file, as it can be neither renamed back nor copied),
# and finishes at the next restart instead, naming every file in use with what has it open. Until then every file of
# the version that was there is as it was (read once the second process has let go, as it lets no one read the file);
# after the restart's work the new version is whole.
$filesBefore = Get-ProgramFiles
$holder = Start-Watcher 'upgrade-aside-held' '*.old' '' 'None'
try {
    $code = Invoke-Setup 'upgrade-aside-held'
    $asideLog = Get-UninstallLog 'upgrade-aside-held'
    $watched = Get-Watched 'upgrade-aside-held'
    $original = $watched -replace '\.\d+\.old$', ''
    Assert-True ($code -eq 3010) "upgrade-aside-held: the setup finishes at the next restart (exit 3010, got $code)"
    Assert-True ($asideLog -match 'Need to restart Windows\? No') 'upgrade-aside-held: the setup asked for no restart'
    Assert-True ($watched -like "$app\*.old" -and $original -ne $watched) "upgrade-aside-held: the second process held an old file set aside ($watched)"
    Assert-True ($asideLog -match ([regex]::Escape($watched) + ' \(in use by [^\r\n]*came into use while Setup was setting the old files aside')) 'upgrade-aside-held: the setup said which file came into use while it set the files aside'
    Assert-True ($asideLog -match [regex]::Escape("Put back $original under a second name of the same file: $watched is in use")) "upgrade-aside-held: the setup put $original back under a second name of the same file"
    Assert-True ($asideLog -match ([regex]::Escape("In use, so it cannot be replaced or removed now: $watched (in use by ") + "[^\r\n]*process $($holder.Id), run by ")) 'upgrade-aside-held: the setup named the file in use and the process that has it open'
    Assert-True ((Test-Path -LiteralPath $original) -and (Test-Path -LiteralPath $launcher) -and (Test-Path -LiteralPath $exe)) "upgrade-aside-held: until the restart the version that was there is in place, $original among its files"
    Assert-True ($asideLog -match 'Windows puts this version of PoolWatch in place the next time it restarts; until then the version you had stays in place\.') 'upgrade-aside-held: the setup said the version that was there stays in place until the restart'
} finally { Stop-Held $holder }
Write-Host 'CHECK-RAN: upgrade-aside-held-before-restart'
$filesAfter = Get-ProgramFiles
# The uninstaller in the install folder itself goes now (the setup wrote its own in a folder of its own: SweepUninstallers).
$changed = @($filesBefore.Keys | Where-Object { $_ -notlike "$app\unins*" -and $filesAfter[$_] -ne $filesBefore[$_] })
$added = @($filesAfter.Keys | Where-Object { -not $filesBefore.ContainsKey($_) -and $_ -ine $watched -and $_ -notlike "$app\at-restart\*" -and $_ -notlike "$app\uninstall\*" })
Assert-True ($changed.Count -eq 0 -and $added.Count -eq 0) "upgrade-aside-held: until the restart every file of the version that was there is as it was ($(@($changed + $added) -join ', '))"
Write-Host 'CHECK-RAN: upgrade-aside-held-files-as-they-were'
$notDone = @(Complete-RestartWork)
Assert-True ((Test-Path -LiteralPath $launcher) -and (Test-Path -LiteralPath $exe) -and (Get-Item $launcher).VersionInfo.ProductVersion -eq (Get-Item $exe).VersionInfo.ProductVersion) 'upgrade-aside-held: after the restart''s work the launcher and PoolWatch itself are of one version'
Write-Host 'CHECK-RAN: upgrade-aside-held-one-version'
$aside = @(Get-ChildItem -LiteralPath $app -Recurse -Force -Filter '*.old' -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
Assert-True ($aside.Count -eq 0 -and -not (Test-Path -LiteralPath (Join-Path $app 'at-restart'))) "upgrade-aside-held: after the restart's work nothing of the setup's own is left ($($aside -join ', '))"
Assert-True ($notDone.Count -eq 0 -and @(Get-RestartWork).Count -eq 0) "upgrade-aside-held: the restart's work in the install folder all went through, and Windows has none left ($($notDone -join ', '))"
Write-Host 'CHECK-RAN: upgrade-aside-held'

# An uninstall whose cleanup cannot run removes no file and puts nothing back: with the installed launcher moved aside,
# the install folder has no launcher of its program's version, so the cleanup is not started, and the silent uninstall
# ends with the install folder, the change and the data folder as they were. The launcher is put back for the uninstall
# after it.
Rename-Item -LiteralPath $launcher -NewName 'PoolWatch.exe.moved'
try {
    $p = Start-Process (Get-Uninstaller) -ArgumentList ($silent + "/LOG=`"$Out\uninstall-blocked.log`"") -Wait -PassThru
    $deadline = (Get-Date).AddMinutes(2)
    while ((Get-CimInstance Win32_Process -Filter "Name LIKE '[_]unins%'") -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
    Write-Host "  uninstall-blocked -> exit $($p.ExitCode)"
    Assert-True ((Get-UninstallLog 'uninstall-blocked') -match 'is not there, or the launcher beside it is of another version, so PoolWatch put nothing back and the uninstaller has removed nothing') 'an uninstall whose cleanup could not be started says so'
    Assert-True ((Test-Path -LiteralPath (Get-Uninstaller)) -and (Test-Path "$launcher.moved") -and (Test-Path $exe) -and (Test-Path $uninstallKey)) 'an uninstall whose cleanup could not be started removed no file'
    Assert-True ((Get-StartMode $service) -eq 'Disabled') "an uninstall whose cleanup could not be started left $service as it was"
    Assert-True (Test-Path $data) 'an uninstall whose cleanup could not be started kept the data folder'
    Write-Host 'CHECK-RAN: uninstall-blocked'
} finally { Rename-Item -LiteralPath "$launcher.moved" -NewName 'PoolWatch.exe' -ErrorAction SilentlyContinue }
Copy-Item -LiteralPath $log "$Out\poolwatch.log" -ErrorAction SilentlyContinue   # uploaded: /DELETEDATA deletes it
# While a process (started suspended, so it holds no name of PoolWatch's and hears no exit signal) runs the exe, and
# without /NORESTART, so that a restart it asked for would restart the runner: the uninstall puts the kept change back,
# chooses to run its cleanup once more (part of PoolWatch may still run after the first), removes every file but the
# exe in use, and leaves that and the folder to Windows to delete at the next restart.
$held = [HeldExe]::StartSuspended($exe, '--tray')
try {
    Uninstall 'uninstall-3' @('/DELETEDATA') @('/VERYSILENT', '/SUPPRESSMSGBOXES')
    $uninstallLog = Get-UninstallLog 'uninstall-3'
    Assert-True ($uninstallLog -match "Uninstalling puts back: Turned off .*\b$([regex]::Escape($service))\b") "the silent /DELETEDATA uninstall listed $service as one it puts back"
    Assert-True ($uninstallLog -match 'Every change is to be put back \(one PoolWatch cannot put back stays\)\.') 'a silent /DELETEDATA uninstall chose to put the changes back'
    Assert-True ((Get-StartMode $service) -eq $before) "the silent /DELETEDATA uninstall put $service back to $before (got $(Get-StartMode $service))"
    Assert-True ($uninstallLog -match 'Part of PoolWatch may still run after its cleanup, so the cleanup is to run once more') 'the uninstall chose to run its cleanup once more'
    Assert-True ($uninstallLog -match 'Need to restart Windows\? No') 'the uninstall asked for no restart'
    Assert-True ($uninstallLog -match [regex]::Escape("Windows deletes it at its next restart: $exe")) 'the uninstall left the exe in use to Windows to delete at the next restart'
    $left = @(Get-ChildItem -LiteralPath $app -Recurse -Force -File -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    Assert-True ($left.Count -eq 1 -and $left[0] -ieq $exe) "the uninstall removed every file but the exe in use (left: $($left -join ', '))"
    $deletes = @((Get-RestartWork) | Where-Object { $_.To -eq '' } | ForEach-Object { $_.From })
    Assert-True (($deletes -contains $exe) -and ($deletes -contains (Split-Path $exe)) -and ($deletes -contains $app)) "Windows deletes the exe, its folder and the install folder at the next restart ($($deletes -join ', '))"
    Assert-True (-not (Test-Path $data)) 'the silent /DELETEDATA uninstall removed the data folder once every change was put back'
} finally { Stop-Held $held }
Write-Host 'CHECK-RAN: uninstall-3'
# The setup run before that restart: Windows' list has the uninstall's deletes in the install folder, which come before
# anything this setup gives it, so it finishes at the restart too, after them, asking for no restart; a folder of the
# install folder that the uninstall gave Windows to delete keeps a file of the setup's until the moves into it are done.
# This test then does that restart's work: the install is whole, and nothing of the setup's own is left.
$code = Invoke-Setup 'reinstall-before-restart'
$setupLog = Get-UninstallLog 'reinstall-before-restart'
Assert-True ($code -eq 3010) "a setup before the uninstall's restart finishes at that restart (exit 3010, got $code)"
Assert-True ($setupLog -match 'Need to restart Windows\? No') 'the setup before the uninstall''s restart asked for no restart'
Assert-True ($setupLog -notmatch 'Found pending rename or delete that matches one of our files') 'the setup before the uninstall''s restart: Inno Setup''s own check found none of its files in Windows'' list'
Assert-True ($setupLog -match [regex]::Escape("An earlier uninstall gave Windows $(Split-Path $exe) to delete at its next restart")) 'the setup kept the app folder, which the uninstall gives Windows to delete, until its moves into it'
Assert-True ($setupLog -match 'Windows puts this version of PoolWatch in place the next time it restarts; until then PoolWatch is not installed: an earlier uninstall removed it') 'the setup before the uninstall''s restart said PoolWatch is not installed until then'
Write-Host 'CHECK-RAN: reinstall-before-restart'
$notDone = @(Complete-RestartWork)
foreach ($f in $launcher, $exe, (Join-Path $app 'app\amd64\KernelTraceControl.dll'), (Get-Uninstaller), $startMenu) {
    Assert-True (Test-Path -LiteralPath $f) "after the uninstall's and the setup's restart work $f is there"
}
$own = @(Get-ChildItem -LiteralPath $app -Recurse -Force -Filter 'at-restart*' -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
Assert-True ($own.Count -eq 0) "after that restart's work nothing of the setup's own is left ($($own -join ', '))"
Assert-True ($notDone.Count -eq 0 -and @(Get-RestartWork).Count -eq 0) "the uninstall's and the setup's restart work in the install folder all went through, and Windows has none left ($($notDone -join ', '))"
Write-Host 'CHECK-RAN: reinstall-before-restart-after-restart'
Uninstall 'uninstall-4'
Assert-True (-not (Test-Path $app)) 'the install folder is gone after the last uninstall'
if (Test-Path $app) { Show-InstallFolder 'uninstall-4' }

Complete-Assertions 'Installer e2e'
