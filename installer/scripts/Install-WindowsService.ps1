[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallDir,

    [string]$ServiceName = 'AiScrapingDefense',

    [string]$DisplayName = 'AI Scraping Defense',

    [string]$Description = 'AI Scraping Defense edge gateway and operator API.',

    [string]$ExecutableName = 'AiScrapingDefense.EdgeGateway.exe',

    [ValidateSet('LocalService', 'NetworkService', 'LocalSystem')]
    [string]$ServiceAccount = 'LocalService',

    [switch]$StartService
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ServiceAccountConfig {
    param([string]$Account)

    switch ($Account) {
        'LocalService' {
            return @{
                Name = 'NT AUTHORITY\LocalService'
                Sid = '*S-1-5-19'
            }
        }
        'NetworkService' {
            return @{
                Name = 'NT AUTHORITY\NetworkService'
                Sid = '*S-1-5-20'
            }
        }
        'LocalSystem' {
            return @{
                Name = 'LocalSystem'
                Sid = '*S-1-5-18'
            }
        }
        default {
            throw "Unsupported service account '$Account'."
        }
    }
}

function Invoke-Sc {
    param([string[]]$Arguments)

    $output = & sc.exe @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $joined = $Arguments -join ' '
        throw "sc.exe $joined failed with exit code $LASTEXITCODE.`n$output"
    }

    return $output
}

$resolvedInstallDir = [System.IO.Path]::GetFullPath($InstallDir)
$exePath = Join-Path $resolvedInstallDir $ExecutableName

if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Service executable not found at '$exePath'."
}

$dataDir = Join-Path $resolvedInstallDir 'data'
$logsDir = Join-Path $resolvedInstallDir 'logs'

New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
New-Item -ItemType Directory -Force -Path $logsDir | Out-Null

$accountConfig = Get-ServiceAccountConfig -Account $ServiceAccount

& icacls.exe $dataDir /grant "$($accountConfig.Sid):(OI)(CI)M" /t | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Failed to grant modify permissions on '$dataDir'."
}

& icacls.exe $logsDir /grant "$($accountConfig.Sid):(OI)(CI)M" /t | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Failed to grant modify permissions on '$logsDir'."
}

$binaryPath = '"{0}" --contentRoot "{1}"' -f $exePath, $resolvedInstallDir
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if ($null -eq $service) {
    Invoke-Sc -Arguments @(
        'create',
        $ServiceName,
        'binPath=', $binaryPath,
        'start=', 'auto',
        'obj=', $accountConfig.Name,
        'DisplayName=', $DisplayName
    ) | Out-Null
}
else {
    if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
        $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
    }

    Invoke-Sc -Arguments @(
        'config',
        $ServiceName,
        'binPath=', $binaryPath,
        'start=', 'auto',
        'obj=', $accountConfig.Name,
        'DisplayName=', $DisplayName
    ) | Out-Null
}

Invoke-Sc -Arguments @('description', $ServiceName, $Description) | Out-Null
Invoke-Sc -Arguments @('failure', $ServiceName, 'reset=', '86400', 'actions=', 'restart/60000/restart/60000/restart/60000') | Out-Null
Invoke-Sc -Arguments @('failureflag', $ServiceName, '1') | Out-Null

if ($StartService.IsPresent) {
    Start-Service -Name $ServiceName -ErrorAction Stop
}