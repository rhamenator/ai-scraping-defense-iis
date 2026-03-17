[CmdletBinding()]
param(
    [string]$ServiceName = 'AiScrapingDefense'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-Sc {
    param([string[]]$Arguments)

    $output = & sc.exe @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $joined = $Arguments -join ' '
        throw "sc.exe $joined failed with exit code $LASTEXITCODE.`n$output"
    }

    return $output
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -eq $service) {
    return
}

if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
    Stop-Service -Name $ServiceName -Force -ErrorAction Stop
    $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
}

Invoke-Sc -Arguments @('delete', $ServiceName) | Out-Null