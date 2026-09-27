<#
.SYNOPSIS
  Builds the release package: artifacts/DndSheet-<version>-win-x64.zip plus artifacts/SHA256SUMS.txt.
  These two files are exactly what a GitHub release must carry for the in-app updater.
.EXAMPLE
  ./scripts/publish.ps1                 # version from Directory.Build.props
  ./scripts/publish.ps1 -Version 1.2.0  # override (CI passes the tag version)
#>
param([string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Version) { $Version = ([xml](Get-Content "$root/Directory.Build.props")).Project.PropertyGroup.Version | Select-Object -First 1 }
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$') { throw "Invalid version '$Version'" }

$artifacts = Join-Path $root 'artifacts'
$stage = Join-Path $artifacts 'publish/DndSheet'
if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }

# Framework-dependent (requires the .NET 10 Desktop Runtime) keeps the package small and lets the
# updater be a single small exe that can be copied out of the install folder to replace it.
dotnet publish "$root/src/DndSheet.App" -c Release -r win-x64 --self-contained false -p:Version=$Version -o $stage
if ($LASTEXITCODE) { throw 'App publish failed' }
dotnet publish "$root/src/DndSheet.Updater" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:Version=$Version -o $stage
if ($LASTEXITCODE) { throw 'Updater publish failed' }
Get-ChildItem $stage -Filter *.pdb | Remove-Item
if (Test-Path "$stage/appsettings.Development.json") { throw 'Development settings must never ship in a release package' }

$zipName = "DndSheet-$Version-win-x64.zip"
Compress-Archive -Path "$stage/*" -DestinationPath (Join-Path $artifacts $zipName)
$hash = (Get-FileHash (Join-Path $artifacts $zipName) -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $artifacts 'SHA256SUMS.txt'), "$hash  $zipName`n")
Write-Host "Package: artifacts/$zipName"
Write-Host "SHA-256: $hash"
