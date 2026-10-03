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
    <# Checks a prerequisite. If it's missing, says where to get it and returns $false; nothing is installed on the user's behalf. #>
    param([string]$Name, [scriptblock]$Test, [string]$DownloadUrl)
    if (& $Test) { Write-Host "[ok] $Name found."; return $true }
    Write-Warning "$Name is required but was not found. Install it from $DownloadUrl and run the installer again."
    return $false
}
