<#
.SYNOPSIS
  Builds the app and installs it for the current user with Start Menu and desktop shortcuts,
  so it launches by double-click like any other program. No admin rights needed.
  Re-run to update a local install from source; installed copies also self-update from GitHub Releases.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts/install.ps1
  powershell -ExecutionPolicy Bypass -File scripts/install.ps1 -NoDesktopShortcut
#>
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\DndSheet'),
    [switch]$NoDesktopShortcut
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

& "$PSScriptRoot/publish.ps1"
$package = Join-Path $root 'artifacts/publish/DndSheet'

if (Get-Process DndSheet -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$InstallDir*" }) {
    throw 'DndSheet is running from the install folder. Close it and run this script again.'
}
# Replace program files only; characters and settings live in %LOCALAPPDATA%\DndSheet, not here.
if (Test-Path $InstallDir) { Get-ChildItem $InstallDir | Remove-Item -Recurse -Force }
New-Item -ItemType Directory -Force $InstallDir | Out-Null
Copy-Item "$package/*" $InstallDir -Recurse

$exe = Join-Path $InstallDir 'DndSheet.exe'
$shell = New-Object -ComObject WScript.Shell
$targets = @([Environment]::GetFolderPath('Programs'))
if (-not $NoDesktopShortcut) { $targets += [Environment]::GetFolderPath('Desktop') }
foreach ($dir in $targets) {
    $link = $shell.CreateShortcut((Join-Path $dir 'DndSheet.lnk'))
    $link.TargetPath = $exe
    $link.WorkingDirectory = $InstallDir
    $link.Description = 'D&D 5e character manager'
    $link.Save()
}
Write-Host "Installed to $InstallDir"
Write-Host 'Launch DndSheet from the Start Menu or desktop shortcut.'
