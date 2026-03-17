[CmdletBinding()]
param(
    [string]$Configuration = 'Release',

    [string]$Runtime = 'win-x64',

    [string]$Version = '0.0.0-local',

    [string]$OutputRoot = 'artifacts\installer',

    [string]$IsccPath,

    [switch]$SkipCompile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$layoutScript = Join-Path $PSScriptRoot 'New-ReleaseLayout.ps1'

& $layoutScript -Configuration $Configuration -Runtime $Runtime -Version $Version -OutputRoot $OutputRoot

$stageDir = Join-Path $repoRoot (Join-Path $OutputRoot 'stage')
$installerOutputDir = Join-Path $repoRoot (Join-Path $OutputRoot 'output')

Remove-Item -LiteralPath $installerOutputDir -Force -Recurse -ErrorAction SilentlyContinue

if ($SkipCompile.IsPresent) {
    Write-Host 'Skipping Inno Setup compilation.'
    return
}

$candidateIsccPaths = @(
    $IsccPath,
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

$resolvedIsccPath = $candidateIsccPaths | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if ($null -eq $resolvedIsccPath) {
    throw 'ISCC.exe was not found. Install Inno Setup 6 or provide -IsccPath.'
}

New-Item -ItemType Directory -Force -Path $installerOutputDir | Out-Null

& $resolvedIsccPath `
    "/DAppVersion=$Version" `
    "/DRuntimeIdentifier=$Runtime" `
    "/DSourceDir=$stageDir" `
    "/DOutputDir=$installerOutputDir" `
    (Join-Path $PSScriptRoot 'AiScrapingDefense.Setup.iss')

if ($LASTEXITCODE -ne 0) {
    throw "ISCC.exe failed with exit code $LASTEXITCODE."
}

$installer = Get-ChildItem -Path $installerOutputDir -Filter '*.exe' | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if ($null -eq $installer) {
    throw 'Installer executable was not found after ISCC compilation.'
}

$hash = Get-FileHash $installer.FullName -Algorithm SHA256
"$($hash.Hash.ToLowerInvariant()) *$($installer.Name)" | Set-Content -LiteralPath "$($installer.FullName).sha256" -Encoding ASCII

Write-Host "Built installer in $installerOutputDir"