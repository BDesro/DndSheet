<#
.SYNOPSIS
  Exits 0 when <Version> in Directory.Build.props is newer than every v<major>.<minor>.<patch> tag on origin
  (that is, newer than the latest release), and 1 otherwise. Used by CI on pull requests into main and by the
  Release workflow, so both agree on what counts as a new version.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$text = ([xml](Get-Content "$root/Directory.Build.props")).Project.PropertyGroup.Version | Select-Object -First 1
if ($text -notmatch '^\d+\.\d+\.\d+$') { Write-Host "Invalid version '$text' in Directory.Build.props."; exit 1 }

$latest = git ls-remote --tags origin 'refs/tags/v*' |
    ForEach-Object { if ($_ -match 'refs/tags/v(\d+\.\d+\.\d+)$') { [version]$Matches[1] } } |
    Sort-Object -Descending | Select-Object -First 1
if ($latest -and [version]$text -le $latest) {
    Write-Host "Version $text must be newer than the latest release $latest. Bump <Version> in Directory.Build.props and rewrite RELEASE_NOTES.md; merging into main releases that version."
    exit 1
}
Write-Host "Version $text is newer than the latest release ($(if ($latest) { $latest } else { 'none' }))."
