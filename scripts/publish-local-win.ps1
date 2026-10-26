#requires -Version 5.1
<#
.SYNOPSIS
  Build the self-contained Windows "localhost mode" package.

.DESCRIPTION
  Publishes RouteBaseWeb as a self-contained win-x64 app (using the publish-only profile already in
  RouteBaseWeb.csproj — activated because PublishDir is set), drops in appsettings.Local.json so it boots
  in localhost mode, adds a friendly launcher, and zips the folder.

  Result: unzip -> double-click "Start RouteBase.bat" (or RouteBaseWeb.exe) -> the app starts on
  http://localhost:5054 and opens in the default browser as a single local user (no server, no login).

.EXAMPLE
  pwsh ./scripts/publish-local-win.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputDir,
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project  = Join-Path $repoRoot 'RouteBaseWeb/RouteBaseWeb.csproj'
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'artifacts/localhost-win-x64' }

Write-Host "Publishing self-contained win-x64 to $OutputDir ..." -ForegroundColor Cyan
if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }

# -o sets the MSBuild PublishDir property, which activates the self-contained win-x64 block in the csproj.
dotnet publish $project -c $Configuration -o $OutputDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# The one file that flips the app into localhost mode. Kept out of the project dir on purpose (the Web SDK
# would otherwise auto-copy appsettings.*.json into every build/server output).
Copy-Item (Join-Path $PSScriptRoot 'appsettings.Local.json') (Join-Path $OutputDir 'appsettings.Local.json') -Force

# Friendly launcher — nicer than hunting for RouteBaseWeb.exe.
@'
@echo off
cd /d "%~dp0"
start "" "RouteBaseWeb.exe"
'@ | Set-Content -Path (Join-Path $OutputDir 'Start RouteBase.bat') -Encoding ASCII

Write-Host "Package ready: $OutputDir" -ForegroundColor Green

if (-not $NoZip) {
    $zip = Join-Path (Split-Path -Parent $OutputDir) 'RouteBase-localhost-win-x64.zip'
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $OutputDir '*') -DestinationPath $zip
    Write-Host "Zipped: $zip" -ForegroundColor Green
}
