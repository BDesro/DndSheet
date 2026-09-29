<#
.SYNOPSIS
  Builds Hearthsheet from source and installs it for the current user (Start Menu + desktop shortcuts).
  Checks for the .NET 10 SDK (needed to build) first and offers to install it; the packaged installer
  then checks for the .NET 10 Desktop Runtime (needed to run).
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts/install.ps1
  powershell -ExecutionPolicy Bypass -File scripts/install.ps1 -NoDesktopShortcut
#>
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\Hearthsheet'),
    [switch]$NoDesktopShortcut,
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'packaging/Dependencies.ps1')

$sdkOk = Confirm-Dependency -Name '.NET 10 SDK' `
    -Test { Test-DotnetComponent 'sdk' 10 } `
    -WingetId 'Microsoft.DotNet.SDK.10' `
    -DownloadUrl 'https://dotnet.microsoft.com/download/dotnet/10.0'
if (-not $sdkOk) { Write-Host 'Build cancelled: the .NET 10 SDK is required.'; exit 1 }

& "$PSScriptRoot/publish.ps1"
& (Join-Path $root 'artifacts/publish/Hearthsheet/Install.ps1') -InstallDir $InstallDir -NoDesktopShortcut:$NoDesktopShortcut -NoLaunch:$NoLaunch
exit $LASTEXITCODE
