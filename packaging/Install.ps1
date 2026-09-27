<#
.SYNOPSIS
  Installs DndSheet for the current user from an extracted release folder.
  Checks for the .NET 10 Desktop Runtime first and offers to install it if it's missing.
  No administrator rights are needed for DndSheet itself.
.PARAMETER InstallDir      Target folder (default %LOCALAPPDATA%\Programs\DndSheet).
.PARAMETER NoDesktopShortcut  Only create the Start Menu shortcut.
.PARAMETER NoLaunch        Don't offer to start the app afterwards.
.PARAMETER AssumeRuntimeMissing  Testing aid: behave as if the runtime check failed.
#>
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\DndSheet'),
    [switch]$NoDesktopShortcut,
    [switch]$NoLaunch,
    [switch]$AssumeRuntimeMissing
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Dependencies.ps1')

$source = $PSScriptRoot
Write-Host "DndSheet installer"
Write-Host "------------------"

if (-not (Test-Path (Join-Path $source 'DndSheet.exe'))) {
    throw "DndSheet.exe was not found next to this installer. Extract the whole release zip, then run Install.cmd from it."
}

$runtimeOk = Confirm-Dependency -Name '.NET 10 Desktop Runtime (x64)' `
    -Test { -not $AssumeRuntimeMissing -and (Test-DotnetComponent 'Microsoft.WindowsDesktop.App' 10) } `
    -WingetId 'Microsoft.DotNet.DesktopRuntime.10' `
    -DownloadUrl 'https://dotnet.microsoft.com/download/dotnet/10.0'
if (-not $runtimeOk) {
    Write-Host 'Installation cancelled: a required component is missing. Nothing was changed.'
    exit 1
}

$target = [IO.Path]::GetFullPath($InstallDir)
if ($target.TrimEnd('\') -eq ([IO.Path]::GetFullPath($source)).TrimEnd('\')) {
    Write-Host "Already running from $target; nothing to copy."
} else {
    if (Get-Process DndSheet -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($target, [StringComparison]::OrdinalIgnoreCase) }) {
        Write-Host 'DndSheet is running from the install folder. Close it and run the installer again.'
        exit 1
    }
    # Only program files live here; characters and settings are in %LOCALAPPDATA%\DndSheet and are untouched.
    if (Test-Path $target) { Get-ChildItem $target | Remove-Item -Recurse -Force }
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $source '*') $target -Recurse
    Write-Host "[ok] Installed to $target"
}

$exe = Join-Path $target 'DndSheet.exe'
$shell = New-Object -ComObject WScript.Shell
$folders = @([Environment]::GetFolderPath('Programs'))
if (-not $NoDesktopShortcut) { $folders += [Environment]::GetFolderPath('Desktop') }
foreach ($folder in $folders) {
    $link = $shell.CreateShortcut((Join-Path $folder 'DndSheet.lnk'))
    $link.TargetPath = $exe
    $link.WorkingDirectory = $target
    $link.Description = 'D&D 5e character manager'
    $link.Save()
}
Write-Host ('[ok] Shortcuts created (Start Menu' + $(if ($NoDesktopShortcut) { ')' } else { ' and desktop)' }))

if (-not $NoLaunch) {
    $answer = Read-Host 'Launch DndSheet now? [Y/n]'
    if (-not $answer -or $answer -match '^(y|yes)$') { Start-Process $exe -WorkingDirectory $target }
}
