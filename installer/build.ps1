# Builds the PoolWatch installer (CI only: the installer and sign jobs). Installs the pinned Inno Setup after checking its
# download, stages the self-contained build with the files that ship next to PoolWatch.exe, and compiles PoolWatch.iss.
# A release passes -SignTool and -Thumbprint: the launcher script is signed before it is packed, and Inno Setup signs the
# setup and its uninstaller as it makes them.
param(
    [Parameter(Mandatory)] [string]$Publish,    # the self-contained build (PoolWatch-selfcontained.exe and its DLLs)
    [Parameter(Mandatory)] [string]$Out,        # a new folder: gets stage\ (what is installed) and the setup
    [string]$SignTool,
    [string]$Thumbprint
)
$ErrorActionPreference = 'Stop'
if ([bool]$SignTool -ne [bool]$Thumbprint) { throw 'Pass both -SignTool and -Thumbprint, or neither' }
$repo = Split-Path $PSScriptRoot -Parent

# Inno Setup 6.7.3, pinned: the SHA-256 was computed once from this download (signed by Pyrsys B.V., Inno Setup's makers).
$innoUrl = 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe'
$innoSha = '9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732'
$inno = Join-Path $env:RUNNER_TEMP 'innosetup'
if (-not (Test-Path "$inno\ISCC.exe")) {
    $download = Join-Path $env:RUNNER_TEMP 'innosetup-6.7.3.exe'
    Invoke-WebRequest -Uri $innoUrl -OutFile $download
    $actual = (Get-FileHash $download -Algorithm SHA256).Hash
    if ($actual -ne $innoSha) { throw "Inno Setup download hash mismatch: got $actual" }
    $p = Start-Process $download -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/DIR=$inno" -Wait -PassThru
    if ($p.ExitCode -ne 0 -or -not (Test-Path "$inno\ISCC.exe")) { throw "Inno Setup did not install (exit $($p.ExitCode))" }
}

# What is installed: the build, the ChatGPT launcher and the licences.
$stage = Join-Path $Out 'stage'
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item -Path (Join-Path $Publish '*') -Destination $stage -Recurse
Copy-Item -Destination $stage -Path "$PSScriptRoot\chatgpt-no-shader-cache.ps1", "$repo\LICENSE", "$repo\THIRD-PARTY-NOTICES.md"
if ($SignTool) {
    & $SignTool sign /fd sha256 /tr http://ts.ssl.com /td sha256 /sha1 $Thumbprint "$stage\chatgpt-no-shader-cache.ps1"
    if ($LASTEXITCODE -ne 0) { throw "signtool failed on the launcher (exit $LASTEXITCODE)" }
}

$version = ([xml](Get-Content "$repo\Directory.Build.props" -Raw)).SelectSingleNode('/Project/PropertyGroup/Version').InnerText
$arguments = @("/DAppVersion=$version", "/DPublishDir=$stage", "/O$Out")
# The same signing for the setup and the uninstaller, run by Inno Setup: $q is a quote, $f the file to sign (quoted).
if ($SignTool) { $arguments += '/DSign', ('/Spwsign=$q' + $SignTool + '$q sign /fd sha256 /tr http://ts.ssl.com /td sha256 /sha1 ' + $Thumbprint + ' $f') }
& "$inno\ISCC.exe" @arguments "$PSScriptRoot\PoolWatch.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit $LASTEXITCODE)" }
$setup = Join-Path $Out "PoolWatch-v$version-setup-win-x64.exe"
if (-not (Test-Path $setup)) { throw "The setup was not made: $setup" }
# Its version info: the file version, as numbers and as text, is the Version above without a pre-release suffix, and
# the copyright is LICENSE's line. Inno Setup writes each text over a fixed run of spaces in its setup and keeps the rest
# of them, so the texts are compared without their trailing spaces.
$info = (Get-Item $setup).VersionInfo
$numbers = ($version -split '-', 2)[0]
$copyright = @(Get-Content "$repo\LICENSE" | Where-Object { $_ -match '^Copyright ' })[0]
$fileVersion = "$($info.FileVersion)".TrimEnd(' ')
$legalCopyright = "$($info.LegalCopyright)".TrimEnd(' ')
if ("$($info.FileMajorPart).$($info.FileMinorPart).$($info.FileBuildPart)" -ne $numbers -or $fileVersion -cne $numbers -or $legalCopyright -cne $copyright) {
    throw "The setup's version info is $($info.FileMajorPart).$($info.FileMinorPart).$($info.FileBuildPart), '$fileVersion' / '$legalCopyright': expected $numbers, '$numbers' / '$copyright' (PoolWatch.iss)"
}
Write-Host "setup: $setup ($((Get-Item $setup).Length) bytes)"
