# Download Warning Guidance

GitHub-built release assets are useful for repeatability, checksums, and release provenance. They do not automatically satisfy Windows SmartScreen or macOS Gatekeeper.

## Windows SmartScreen

If the Windows installer is unsigned, SmartScreen may show an "unrecognized app" warning.

Recommended operator workflow:

1. Download the installer from the GitHub Release page.
2. Verify the SHA-256 checksum against the published `.sha256` file.
3. Open the installer.
4. If SmartScreen appears, choose `More info` and then `Run anyway`.

If the file was marked as downloaded from the internet and you want to clear the zone marker after verification, you can also run:

```powershell
Unblock-File .\ai-scraping-defense-1.0.0-windows-x64-setup.exe
```

What removes the warning for normal users:

- Authenticode signing with a trusted code-signing certificate
- accumulated SmartScreen reputation for that signed publisher and binary lineage

## macOS Gatekeeper

If the macOS package is unsigned or not notarized, Finder may block it with a Gatekeeper warning.

Recommended operator workflow:

1. Download the package from the GitHub Release page.
2. Verify the SHA-256 checksum against the published `.sha256` file.
3. Use one of the bypass methods below.

Finder-based bypass:

1. Control-click the `.pkg` file.
2. Choose `Open`.
3. Confirm the second `Open` prompt.

System Settings bypass:

1. Attempt to open the package once.
2. Open `System Settings` > `Privacy & Security`.
3. Use `Open Anyway` for the blocked package.

Terminal-based bypass after verification:

```bash
xattr -d com.apple.quarantine ./ai-scraping-defense-1.0.0-osx-arm64.pkg
sudo installer -pkg ./ai-scraping-defense-1.0.0-osx-arm64.pkg -target /
```

What removes the warning for normal users:

- Developer ID signing
- notarization by Apple

## Important Distinction

GitHub release automation helps with:

- reproducible builds
- attached checksums
- release history
- clearer provenance for technical reviewers

It does not itself grant publisher trust to Windows or macOS.
