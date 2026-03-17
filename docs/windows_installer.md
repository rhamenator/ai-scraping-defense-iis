# Windows Installer

This repository now includes a Windows installer path for the commercial .NET runtime in `RedisBlocklistMiddlewareApp`.

The installer builds a self-contained `win-x64` publish, copies the runtime into a staged layout, and packages it as an Inno Setup `.exe` that can install and start a Windows service named `AiScrapingDefense`.

## Prerequisites

- Windows x64
- .NET SDK `10.0.104` for building the publish output
- Inno Setup 6 for compiling the installer executable
- Administrative rights on the target host for service installation
- Reachable Redis, and optional PostgreSQL if Markov tarpit mode is enabled

## Build The Installer

From the repository root:

```powershell
.\installer\Build-WindowsInstaller.ps1 -Version 1.0.0
```

Outputs:

- staged files: `artifacts\installer\stage`
- installer executable: `artifacts\installer\output`

If you want to inspect the staged payload without compiling Inno Setup:

```powershell
.\installer\Build-WindowsInstaller.ps1 -Version 1.0.0 -SkipCompile
```

## What The Installer Does

- copies the self-contained `win-x64` publish to `C:\Program Files\AiScrapingDefense`
- creates `data` and `logs` directories under the install root
- installs a Windows service named `AiScrapingDefense`
- configures the service for automatic startup and restart-on-failure behavior

The setup can optionally start the service at the end of installation, but that should only be selected after valid production configuration is in place.

The service runs as `LocalService` by default. The installer grants that identity modify rights on the `data` and `logs` directories so the app can persist SQLite audit data and tarpit artifacts.

## Configuration Notes

The installer does not inject environment-specific secrets. Before or immediately after installation, set the production values in the installed `appsettings.json` or `appsettings.Production.json` for:

- `DefenseEngine:Redis:ConnectionString`
- `DefenseEngine:Management:ApiKey`
- `DefenseEngine:Intake:ApiKey` if `/analyze` is enabled
- `DefenseEngine:Audit:DatabasePath` if you want to override the default relative SQLite path
- `DefenseEngine:Networking:*` if the service runs behind a proxy or CDN

On a fresh machine, the default Redis configuration points at loopback and is intentionally rejected in `Production`. That means service startup will fail until you either supply a real Redis connection string or explicitly allow the loopback configuration for a non-production lab host.

If you change configuration after install, restart the Windows service:

```powershell
Restart-Service AiScrapingDefense
```

To start the service for the first time after configuration:

```powershell
Start-Service AiScrapingDefense
```

## Manual Service Operations

The installer includes helper scripts in the installed `installer\scripts` directory.

Reinstall or update the service registration in place:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\scripts\Install-WindowsService.ps1 -InstallDir "C:\Program Files\AiScrapingDefense" -StartService
```

Remove the service registration without uninstalling files:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\scripts\Uninstall-WindowsService.ps1
```

## Smoke Check

After installation:

```powershell
Invoke-WebRequest http://localhost:8080/health
```

If the service does not come up, inspect:

- Windows Event Viewer
- `Get-Service AiScrapingDefense`
- the installed configuration files under `C:\Program Files\AiScrapingDefense`
- `docs/operator_runbook.md` for runtime validation and endpoint checks

## Signing And SmartScreen

Unsigned installers can still show SmartScreen warnings even when they were produced by GitHub Actions. GitHub-hosted builds help with provenance and reproducibility, but SmartScreen reputation comes from Authenticode signing and publisher reputation, not from the CI provider.

See [download_warnings.md](download_warnings.md) for operator-facing bypass guidance and checksum-verification steps.
