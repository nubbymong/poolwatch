# Shared helpers for the Windows CI scripts.
$ErrorActionPreference = 'Stop'

$script:Failures = New-Object System.Collections.Generic.List[string]

function Assert-True([bool]$cond, [string]$message) {
    if ($cond) { Write-Host "  [PASS] $message" }
    else { Write-Host "  [FAIL] $message"; $script:Failures.Add($message) }
}

function Complete-Assertions([string]$name) {
    if ($script:Failures.Count -gt 0) {
        Write-Host "$name : $($script:Failures.Count) assertion(s) failed:"
        $script:Failures | ForEach-Object { Write-Host "   - $_" }
        exit 1
    }
    Write-Host "$name : all assertions passed"
    exit 0 # not the exit code of the last native command (e.g. an fltmc load of a filter the runner lacks)
}

# The CI runs PoolWatch elevated from the build output, which is not an administrator-only folder: S4 refuses that
# unless asked explicitly (review round 2). Every PoolWatch.exe started by the CI scripts passes this.
$script:AnyLocation = '--allow-any-location'

# Starts PoolWatch.exe (a WinExe) and returns the process. -NoNewWindow keeps it attached to this console.
function Start-PoolWatch([string]$exe, [string[]]$arguments) {
    $arguments = @($arguments) + $script:AnyLocation
    Write-Host "> $exe $($arguments -join ' ')"
    return Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -NoNewWindow
}

function Wait-PoolWatch($proc, [int]$timeoutSeconds) {
    if (-not $proc.WaitForExit($timeoutSeconds * 1000)) {
        $proc.Kill()
        throw "PoolWatch did not exit within $timeoutSeconds s"
    }
    $proc.WaitForExit()
    return $proc.ExitCode
}

function Import-PwCsv([string]$path) {
    if (-not (Test-Path $path)) { throw "missing $path" }
    return @(Import-Csv $path)
}
