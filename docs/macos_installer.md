# macOS Installer

This repository includes a macOS packaging path for the pure-.NET runtime.

The macOS build produces separate `.pkg` installers for:

- `osx-x64`
- `osx-arm64`

For this application shape, `.pkg` is a better fit than `.dmg` because the install needs to place files under system locations and register a `launchd` service definition.

## Prerequisites

- macOS with the .NET SDK `10.0.104`
- Xcode Command Line Tools so `pkgbuild` is available
- Administrative rights on the target host for package installation and `launchd` registration
- Reachable Redis, and optional PostgreSQL if Markov tarpit mode is enabled

## Build The Packages

From the repository root on macOS:

```bash
./installer/macos/build-macos-packages.sh 1.0.0
```

Outputs:

- publish outputs: `artifacts/macos-installer/publish/osx-x64` and `artifacts/macos-installer/publish/osx-arm64`
- package artifacts: `artifacts/macos-installer/output`

## What The Package Installs

- application files under `/usr/local/lib/ai-scraping-defense/<runtime>`
- helper scripts under `/usr/local/lib/ai-scraping-defense/<runtime>/installer/scripts`
- a `launchd` plist at `/Library/LaunchDaemons/com.aiscrapingdefense.edgegateway.plist`

The package does not auto-start the service. That is intentional because a fresh install still needs real production configuration.

The installer also creates writable runtime state directories under:

- `/usr/local/var/lib/ai-scraping-defense`
- `/usr/local/var/log/ai-scraping-defense`

When the service runs from the packaged macOS install root, the built-in defaults automatically remap to those writable locations:

- `DefenseEngine:Audit:DatabasePath` defaults to `/usr/local/var/lib/ai-scraping-defense/defense-events.db`
- `DefenseEngine:Tarpit:ArchiveDirectory` defaults to `/usr/local/var/lib/ai-scraping-defense/tarpit-archives`

Explicit custom paths in configuration are preserved.

## Configuration Notes

Before starting the service, update the installed configuration for:

- `DefenseEngine:Redis:ConnectionString`
- `DefenseEngine:Management:ApiKey`
- `DefenseEngine:Intake:ApiKey` if `/analyze` is enabled
- `DefenseEngine:Networking:*` if the service runs behind a proxy or CDN

Installed paths are runtime-specific, so edit the matching architecture directory:

- `/usr/local/lib/ai-scraping-defense/osx-x64/appsettings.json`
- `/usr/local/lib/ai-scraping-defense/osx-arm64/appsettings.json`

As with Windows, the default loopback Redis configuration is rejected in `Production` unless you explicitly opt in for a lab environment.

## Service Operations

Start the service:

```bash
sudo launchctl bootstrap system /Library/LaunchDaemons/com.aiscrapingdefense.edgegateway.plist
sudo launchctl kickstart -k system/com.aiscrapingdefense.edgegateway
```

Stop the service:

```bash
sudo launchctl bootout system/com.aiscrapingdefense.edgegateway
```

Helper scripts are also installed at:

- `/usr/local/lib/ai-scraping-defense/<runtime>/installer/scripts/start-service.sh`
- `/usr/local/lib/ai-scraping-defense/<runtime>/installer/scripts/stop-service.sh`

## Smoke Check

After the service is started:

```bash
curl http://localhost:8080/health
```

## Signing And Gatekeeper

Unsigned `.pkg` files will trigger Gatekeeper warnings. Building on GitHub Actions improves provenance and repeatability, but it does not remove Gatekeeper prompts. To avoid those warnings for normal end users, you need:

- an Apple Developer account
- a Developer ID Installer certificate
- usually notarization as well

Without those, the package is still usable for technical operators who are willing to bypass Gatekeeper after verifying the checksum and release source.
