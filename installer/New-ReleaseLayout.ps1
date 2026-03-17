[CmdletBinding()]
param(
    [string]$Configuration = 'Release',

    [string]$Runtime = 'win-x64',

    [string]$Version = '0.0.0-local',

    [string]$OutputRoot = 'artifacts\installer'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot (Join-Path $OutputRoot 'publish')
$stageDir = Join-Path $repoRoot (Join-Path $OutputRoot 'stage')

Remove-Item -LiteralPath $publishDir -Force -Recurse -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $stageDir -Force -Recurse -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

dotnet publish (Join-Path $repoRoot 'RedisBlocklistMiddlewareApp\RedisBlocklistMiddlewareApp.csproj') `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    /p:PublishReadyToRun=true `
    /p:Version=$Version `
    -o $publishDir

Copy-Item -Path (Join-Path $publishDir '*') -Destination $stageDir -Recurse -Force

$docDir = Join-Path $stageDir 'docs'
$installerScriptDir = Join-Path $stageDir 'installer\scripts'
New-Item -ItemType Directory -Force -Path $docDir | Out-Null
New-Item -ItemType Directory -Force -Path $installerScriptDir | Out-Null

Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $stageDir -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $stageDir -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\operator_runbook.md') -Destination $docDir -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\windows_installer.md') -Destination $docDir -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'installer\scripts\Install-WindowsService.ps1') -Destination $installerScriptDir -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'installer\scripts\Uninstall-WindowsService.ps1') -Destination $installerScriptDir -Force

@{
    Version = $Version
    Runtime = $Runtime
    PublishDir = $publishDir
    StageDir = $stageDir
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stageDir 'installer-manifest.json') -Encoding ASCII

Write-Host "Staged Windows release layout at $stageDir"