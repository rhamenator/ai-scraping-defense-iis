# Release Artifacts

This document defines the commercial release artifact policy for the .NET stack.

## Registry

Tagged releases publish container images to GitHub Container Registry:

```text
ghcr.io/<owner>/ai-scraping-defense-iis
```

For direct host deployments, the repository also includes:

- a Windows Inno Setup build path described in [windows_installer.md](windows_installer.md) and automated by [.github/workflows/windows-installer.yml](../.github/workflows/windows-installer.yml)
- a macOS `.pkg` build path described in [macos_installer.md](macos_installer.md) and automated by [.github/workflows/macos-installer.yml](../.github/workflows/macos-installer.yml)

On tag builds, the workflow also uploads:

- Windows installer `.exe` assets for `win-x64` and `win-arm64`
- matching SHA-256 checksum files for the Windows installer assets
- macOS `.pkg` assets for `osx-x64` and `osx-arm64`
- matching SHA-256 checksum files for the macOS package assets

The release workflow is implemented in [release-images.yml](../.github/workflows/release-images.yml) and runs on Git tags that match `v*`.

## Tagging Policy

For a stable release tag such as `v1.4.2`, the workflow publishes:

- `ghcr.io/<owner>/ai-scraping-defense-iis:v1.4.2`
- `ghcr.io/<owner>/ai-scraping-defense-iis:1.4.2`
- `ghcr.io/<owner>/ai-scraping-defense-iis:1.4`
- `ghcr.io/<owner>/ai-scraping-defense-iis:latest`

For a prerelease tag such as `v1.4.2-rc.1`, the workflow publishes only:

- `ghcr.io/<owner>/ai-scraping-defense-iis:v1.4.2-rc.1`
- `ghcr.io/<owner>/ai-scraping-defense-iis:1.4.2-rc.1`

This keeps prereleases from mutating `latest` or the rolling minor tag.

## Release Metadata

Each tagged build publishes:

- an OCI image in GHCR
- direct-host installer assets on the GitHub Release page
- OCI labels for source repository, revision, and version
- a Sigstore keyless signature over the pushed image digest
- a GitHub build-provenance attestation pushed to the registry
- a BuildKit SBOM attestation from the image build step

Installer signing is optional and secret-driven:

- Windows installer signing runs when `WINDOWS_SIGN_CERT_BASE64` and `WINDOWS_SIGN_CERT_PASSWORD` are configured in GitHub Actions secrets.
- macOS package signing runs when `MACOS_INSTALLER_CERT_BASE64`, `MACOS_INSTALLER_CERT_PASSWORD`, and `MACOS_INSTALLER_SIGN_IDENTITY` are configured.

Without those secrets, the installers are still built and published, but they remain unsigned and will continue to trigger SmartScreen or Gatekeeper warnings.

## Verification

Verify GitHub provenance:

```bash
gh attestation verify \
  oci://ghcr.io/<owner>/ai-scraping-defense-iis:v1.4.2 \
  --repo <owner>/ai-scraping-defense-iis
```

Verify the Sigstore keyless signature for a released digest:

```bash
cosign verify \
  ghcr.io/<owner>/ai-scraping-defense-iis@sha256:<digest> \
  --certificate-identity https://github.com/<owner>/ai-scraping-defense-iis/.github/workflows/release-images.yml@refs/tags/v1.4.2 \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
```

## Upgrade Path

Recommended operator behavior:

1. Track release notes and changelog entries for the target version.
2. Pull and verify the released image by digest, not just by mutable tag.
3. For production, pin deployments to the verified digest.
4. Roll forward within the same major version unless release notes explicitly state otherwise.
5. Keep the previous verified digest available for rollback.

## Commercial v1 Boundary

The release workflow is intentionally narrow for commercial v1:

- one runtime image
- one Windows installer build path for direct host installs
- one macOS package build path for direct host installs
- one primary registry (`ghcr.io`)
- one signed and attestable release path driven by Git tags

If later commercial packaging requires additional registries, separate base images, or OS-specific variants, that should be tracked as explicit post-v1 work rather than folded into this baseline workflow silently.
