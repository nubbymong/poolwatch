# Starts the ChatGPT desktop app without its GPU shader disk cache (--disable-gpu-shader-disk-cache), through Windows'
# own app activation, so it keeps its package identity, sign-in and data. Installed next to PoolWatch.exe and started,
# as you and not as administrator, by the desktop shortcut "ChatGPT (no shader cache)" that PoolWatch can create. It
# changes nothing on the PC.
#
# Faarfarin traced the app's GPU shader-cache file storm and found this workaround:
# https://github.com/openai/codex/issues/31212#issuecomment-5635622083
#
#   ChatGPT not running           -> start it with the flag; warn only if the flag did not arrive
#   ChatGPT running with the flag -> bring it to the front
#   ChatGPT running without it    -> ask you to quit it first (never closes it for you)
# -Check prints what it would do and changes nothing.
param([switch]$Check)
$flag  = '--disable-gpu-shader-disk-cache'
$title = 'ChatGPT (no shader cache)'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
[ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IApplicationActivationManager {
  [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, int options, out uint processId);
}
[ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")] public class ApplicationActivationManager { }
public static class ChatGptActivation {
  public static uint Start(string aumid, string args) {
    var m = (IApplicationActivationManager)new ApplicationActivationManager();
    uint pid; int hr = m.ActivateApplication(aumid, args, 0, out pid);
    if (hr != 0) Marshal.ThrowExceptionForHR(hr);
    return pid;
  }
}
"@

function Say($msg, $icon) {
  if ($Check) { Write-Host "[would show] $msg"; return }
  [System.Windows.Forms.MessageBox]::Show($msg, $title, 'OK', $icon, 'Button1', 'DefaultDesktopOnly') | Out-Null
}

# The ChatGPT desktop app's package (OpenAI.Codex, or the older OpenAI.ChatGPT-Desktop) and its ChatGPT.exe application.
$package = @(Get-AppxPackage -Name 'OpenAI.Codex') + @(Get-AppxPackage -Name 'OpenAI.ChatGPT-Desktop') | Select-Object -First 1
$app = if ($package) {
  @((Get-AppxPackageManifest $package).Package.Applications.Application) |
    Where-Object { $_.Executable -and (Split-Path $_.Executable -Leaf) -eq 'ChatGPT.exe' } | Select-Object -First 1
}
if (-not $app) { Say 'The ChatGPT desktop app is not installed.' 'Warning'; exit 1 }
$aumid = "$($package.PackageFamilyName)!$($app.Id)"

# The app's main process: ChatGPT.exe from this package, in any version's folder (an update can leave the running one in
# the previous version's), without --type= (its helper processes have one).
$folder = "*\WindowsApps\$($package.Name)_*__$($package.PublisherId)\*"
function Get-ChatGptMain {
  @(Get-CimInstance Win32_Process -Filter "Name='ChatGPT.exe'" |
    Where-Object { $_.ExecutablePath -like $folder -and $_.CommandLine -notmatch '--type=' })
}

$main = @(Get-ChatGptMain)
if ($main.Count -gt 0) {
  if ($main | Where-Object { $_.CommandLine -match [regex]::Escape($flag) }) {
    if ($Check) { Write-Host 'ChatGPT is running WITH the flag -> would bring it to the front.'; exit 0 }
    try { [ChatGptActivation]::Start($aumid, '') | Out-Null }
    catch { Say "Could not bring ChatGPT to the front:`n$($_.Exception.Message)" 'Error'; exit 1 }
    exit 0
  }
  Say "ChatGPT is already running in normal mode.`n`nQuit it fully first (right-click its tray icon, then Quit), then double-click this shortcut again." 'Warning'
  exit 1
}

if ($Check) { Write-Host 'ChatGPT is not running -> would start it with the flag.'; exit 0 }
try { [ChatGptActivation]::Start($aumid, $flag) | Out-Null }
catch { Say "Could not start ChatGPT:`n$($_.Exception.Message)" 'Error'; exit 1 }

for ($i = 0; $i -lt 30; $i++) { Start-Sleep -Seconds 1; $main = @(Get-ChatGptMain); if ($main.Count -gt 0) { break } }
if ($main.Count -eq 0) { Say 'ChatGPT did not appear to start. Try the shortcut again.' 'Warning'; exit 1 }
if (-not ($main | Where-Object { $_.CommandLine -match [regex]::Escape($flag) })) {
  Say "ChatGPT started, but WITHOUT the no-shader-cache setting.`n`nQuit it fully and double-click this shortcut again." 'Warning'
  exit 1
}
