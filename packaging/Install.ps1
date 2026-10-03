<#
.SYNOPSIS
  Installs Hearthsheet for the current user from an extracted release folder.
  Checks for the .NET 10 Desktop Runtime first and points to its download page if it's missing.
  No administrator rights are needed for Hearthsheet itself.
.PARAMETER InstallDir      Target folder (default %LOCALAPPDATA%\Programs\Hearthsheet).
.PARAMETER NoDesktopShortcut  Only create the Start Menu shortcut.
.PARAMETER NoLaunch        Don't offer to start the app afterwards.
#>
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\Hearthsheet'),
    [switch]$NoDesktopShortcut,
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Dependencies.ps1')

$source = $PSScriptRoot
Write-Host "Hearthsheet installer"
Write-Host "------------------"

if (-not (Test-Path (Join-Path $source 'Hearthsheet.exe'))) {
    throw "Hearthsheet.exe was not found next to this installer. Extract the whole release zip, then run Install.cmd from it."
}

$runtimeOk = Confirm-Dependency -Name '.NET 10 Desktop Runtime (x64)' `
    -Test { Test-DotnetComponent 'Microsoft.WindowsDesktop.App' 10 } `
    -DownloadUrl 'https://dotnet.microsoft.com/download/dotnet/10.0'
if (-not $runtimeOk) {
    Write-Host 'Installation cancelled: a required component is missing. Nothing was changed.'
    exit 1
}

$target = [IO.Path]::GetFullPath($InstallDir)
if ($target.TrimEnd('\') -eq ([IO.Path]::GetFullPath($source)).TrimEnd('\')) {
    Write-Host "Already running from $target; nothing to copy."
} else {
    if (Get-Process Hearthsheet -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($target, [StringComparison]::OrdinalIgnoreCase) }) {
        Write-Host 'Hearthsheet is running from the install folder. Close it and run the installer again.'
        exit 1
    }
    # Only program files live here; characters and settings are in %LOCALAPPDATA%\Hearthsheet and are untouched.
    if (Test-Path $target) { Get-ChildItem $target | Remove-Item -Recurse -Force }
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $source '*') $target -Recurse
    Write-Host "[ok] Installed to $target"
}

$exe = Join-Path $target 'Hearthsheet.exe'
$shell = New-Object -ComObject WScript.Shell
$folders = @([Environment]::GetFolderPath('Programs'))
if (-not $NoDesktopShortcut) { $folders += [Environment]::GetFolderPath('Desktop') }
foreach ($folder in $folders) {
    $link = $shell.CreateShortcut((Join-Path $folder 'Hearthsheet.lnk'))
    $link.TargetPath = $exe
    $link.WorkingDirectory = $target
    $link.Description = 'D&D 5e character manager'
    $link.Save()
}
Write-Host ('[ok] Shortcuts created (Start Menu' + $(if ($NoDesktopShortcut) { ')' } else { ' and desktop)' }))

if (-not $NoLaunch) {
    $answer = Read-Host 'Launch Hearthsheet now? [Y/n]'
    if (-not $answer -or $answer -match '^(y|yes)$') { Start-Process $exe -WorkingDirectory $target }
}
