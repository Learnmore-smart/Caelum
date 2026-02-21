<#
.SYNOPSIS
    Build script that works around the WinUI XAML compiler's inability to handle
    Unicode (CJK) characters in file paths.

.DESCRIPTION
    The .NET Framework XamlCompiler.exe bundled with Windows App SDK cannot process
    file paths containing non-ASCII characters. This script creates a temporary
    directory junction at C:\ws\wn pointing to the project root, builds from there,
    and cleans up on exit.

.PARAMETER Configuration
    Build configuration (Debug or Release). Default: Release.

.PARAMETER Clean
    Run 'dotnet clean' before building.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Configuration Debug
    .\build.ps1 -Clean
#>
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$Clean
)

$ErrorActionPreference = "Stop"
$projectDir = $PSScriptRoot
$junctionBase = "C:\ws"
$junctionPath = "$junctionBase\wn"

# Check if the path contains non-ASCII characters
$needsJunction = $projectDir -match '[^\x00-\x7F]'

if ($needsJunction) {
    Write-Host "Project path contains Unicode characters; creating junction at $junctionPath" -ForegroundColor Yellow

    if (-not (Test-Path $junctionBase)) {
        New-Item -ItemType Directory -Path $junctionBase -Force | Out-Null
    }

    # Remove existing junction if it points elsewhere
    if (Test-Path $junctionPath) {
        $existing = (Get-Item $junctionPath).Target
        if ($existing -ne $projectDir) {
            cmd /c "rmdir `"$junctionPath`"" 2>$null
        }
    }

    if (-not (Test-Path $junctionPath)) {
        cmd /c "mklink /J `"$junctionPath`" `"$projectDir`""
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to create junction. Run as Administrator or move project to an ASCII path."
            exit 1
        }
    }

    $buildDir = $junctionPath
} else {
    $buildDir = $projectDir
}

Push-Location $buildDir
try {
    if ($Clean) {
        Write-Host "Cleaning..." -ForegroundColor Cyan
        dotnet clean .\WindowsNotesApp.csproj -c $Configuration 2>&1 | Out-Null
    }

    Write-Host "Building ($Configuration)..." -ForegroundColor Cyan
    dotnet build .\WindowsNotesApp.csproj -c $Configuration

    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build FAILED" -ForegroundColor Red
        exit $LASTEXITCODE
    }

    Write-Host "Build succeeded!" -ForegroundColor Green
} finally {
    Pop-Location
}
