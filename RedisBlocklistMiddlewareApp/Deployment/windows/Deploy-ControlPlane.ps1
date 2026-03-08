param(
    [string]$SiteName = "Default Web Site",
    [string]$AppPath = "control-plane",
    [string]$PublishPath = "C:\inetpub\apps\ai-scraping-defense-iis"
)

Write-Host "Deploying AI Scraping Defense IIS Control Plane..."

if (-not (Test-Path $PublishPath)) {
    New-Item -ItemType Directory -Path $PublishPath -Force | Out-Null
}

Write-Host "Publish path ready: $PublishPath"
Write-Host "Use 'dotnet publish' output and map IIS application '$AppPath' under site '$SiteName'."
Write-Host "Ensure DefenseEngine settings are set via appsettings.Production.json or environment variables."
