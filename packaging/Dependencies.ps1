# Shared prerequisite checks for the installers. Dot-source this file.

function Test-DotnetComponent {
    <# True when a .NET component (e.g. 'Microsoft.WindowsDesktop.App' or 'sdk') with the given major version is installed. #>
    param([string]$Component, [int]$Major)
    $roots = @($env:DOTNET_ROOT, (Join-Path $env:ProgramFiles 'dotnet'), (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet')) |
        Where-Object { $_ }
    foreach ($root in $roots) {
        $dir = if ($Component -eq 'sdk') { Join-Path $root 'sdk' } else { Join-Path $root "shared\$Component" }
        if (Get-ChildItem $dir -Directory -Filter "$Major.*" -ErrorAction SilentlyContinue) { return $true }
    }
    return $false
}

function Confirm-Dependency {
    <#
    Checks a prerequisite; if it's missing, asks the user whether to install it (winget, or the download page),
    then re-checks. Returns $true only when the prerequisite is present afterwards.
    The user sees and accepts winget's own license prompts; nothing is accepted on their behalf.
    #>
    param(
        [string]$Name,
        [scriptblock]$Test,
        [string]$WingetId,
        [string]$DownloadUrl
    )
    if (& $Test) { Write-Host "[ok] $Name found."; return $true }

    Write-Host ""
    Write-Warning "$Name is required but was not found."
    $answer = Read-Host "Install $Name now? [Y/n]"
    if ($answer -and $answer -notmatch '^(y|yes)$') {
        Write-Host "Not installed. Install it from $DownloadUrl and run the installer again."
        return $false
    }

    if (Get-Command winget -ErrorAction SilentlyContinue) {
        Write-Host "Installing $Name with winget (you may be asked to accept its license)..."
        winget install --id $WingetId --exact --source winget
    } else {
        Write-Host "winget isn't available, so the download page will open. Finish that installer, then press Enter here."
        Start-Process $DownloadUrl
        Read-Host 'Press Enter once the installation has finished' | Out-Null
    }

    if (& $Test) { Write-Host "[ok] $Name installed."; return $true }
    Write-Warning "$Name still wasn't found. If the installer asked for a restart, restart and run this installer again."
    return $false
}
