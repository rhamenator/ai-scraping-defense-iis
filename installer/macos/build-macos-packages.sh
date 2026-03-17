#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

VERSION="${1:-0.0.0-local}"
OUTPUT_ROOT="${2:-artifacts/macos-installer}"
CONFIGURATION="${CONFIGURATION:-Release}"
SERVICE_LABEL="com.aiscrapingdefense.edgegateway"
SERVICE_USER="nobody"
SERVICE_GROUP="nobody"
TIMESTAMP_UTC="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

rm -rf "${REPO_ROOT}/${OUTPUT_ROOT}/output"
mkdir -p "${REPO_ROOT}/${OUTPUT_ROOT}/output"

publish_runtime() {
  local runtime="$1"
  local publish_dir="${REPO_ROOT}/${OUTPUT_ROOT}/publish/${runtime}"
  local stage_dir="${REPO_ROOT}/${OUTPUT_ROOT}/stage/${runtime}"
  local pkg_root="${stage_dir}/pkgroot"
  local scripts_dir="${stage_dir}/scripts"
  local install_dir="${pkg_root}/usr/local/lib/ai-scraping-defense/${runtime}"
  local launchd_dir="${pkg_root}/Library/LaunchDaemons"
  local pkg_output_dir="${REPO_ROOT}/${OUTPUT_ROOT}/output"
  local pkg_name="ai-scraping-defense-${VERSION}-${runtime}.pkg"
  local pkg_path="${pkg_output_dir}/${pkg_name}"
  local launchd_plist="${launchd_dir}/${SERVICE_LABEL}.plist"
  local data_dir="/usr/local/var/lib/ai-scraping-defense"
  local log_dir="/usr/local/var/log/ai-scraping-defense"
  local executable_path="/usr/local/lib/ai-scraping-defense/${runtime}/AiScrapingDefense.EdgeGateway"

  rm -rf "${publish_dir}" "${stage_dir}"
  mkdir -p "${publish_dir}" "${install_dir}" "${launchd_dir}" "${scripts_dir}" "${pkg_output_dir}"

  dotnet publish "${REPO_ROOT}/RedisBlocklistMiddlewareApp/RedisBlocklistMiddlewareApp.csproj" \
    -c "${CONFIGURATION}" \
    -r "${runtime}" \
    --self-contained true \
    /p:PublishReadyToRun=true \
    /p:Version="${VERSION}" \
    -o "${publish_dir}"

  cp -R "${publish_dir}/." "${install_dir}/"
  mkdir -p "${install_dir}/docs" "${install_dir}/installer/scripts"
  cp "${REPO_ROOT}/README.md" "${install_dir}/"
  cp "${REPO_ROOT}/LICENSE" "${install_dir}/"
  cp "${REPO_ROOT}/docs/macos_installer.md" "${install_dir}/docs/"
  cp "${REPO_ROOT}/docs/download_warnings.md" "${install_dir}/docs/"

  cat > "${install_dir}/installer/scripts/start-service.sh" <<EOF
#!/usr/bin/env bash
set -euo pipefail
sudo launchctl bootstrap system /Library/LaunchDaemons/${SERVICE_LABEL}.plist
sudo launchctl kickstart -k system/${SERVICE_LABEL}
EOF

  cat > "${install_dir}/installer/scripts/stop-service.sh" <<EOF
#!/usr/bin/env bash
set -euo pipefail
sudo launchctl bootout system/${SERVICE_LABEL} || true
EOF

  chmod +x "${install_dir}/installer/scripts/start-service.sh" "${install_dir}/installer/scripts/stop-service.sh"

  cat > "${launchd_plist}" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>${SERVICE_LABEL}</string>
  <key>ProgramArguments</key>
  <array>
    <string>${executable_path}</string>
    <string>--contentRoot</string>
    <string>/usr/local/lib/ai-scraping-defense/${runtime}</string>
  </array>
  <key>EnvironmentVariables</key>
  <dict>
    <key>ASPNETCORE_URLS</key>
    <string>http://0.0.0.0:8080</string>
    <key>DOTNET_ENVIRONMENT</key>
    <string>Production</string>
  </dict>
  <key>WorkingDirectory</key>
  <string>/usr/local/lib/ai-scraping-defense/${runtime}</string>
  <key>RunAtLoad</key>
  <false/>
  <key>KeepAlive</key>
  <true/>
  <key>UserName</key>
  <string>${SERVICE_USER}</string>
  <key>StandardOutPath</key>
  <string>${log_dir}/stdout.log</string>
  <key>StandardErrorPath</key>
  <string>${log_dir}/stderr.log</string>
</dict>
</plist>
EOF

  cat > "${scripts_dir}/postinstall" <<EOF
#!/usr/bin/env bash
set -euo pipefail
mkdir -p "${data_dir}" "${log_dir}"
chmod 755 "/usr/local/lib/ai-scraping-defense/${runtime}"
chown -R ${SERVICE_USER}:${SERVICE_GROUP} "${data_dir}" "${log_dir}" 2>/dev/null || true
chmod 775 "${data_dir}" "${log_dir}"
chmod 644 "/Library/LaunchDaemons/${SERVICE_LABEL}.plist"
exit 0
EOF

  chmod +x "${scripts_dir}/postinstall"

  pkgbuild \
    --root "${pkg_root}" \
    --identifier "${SERVICE_LABEL}.${runtime}" \
    --version "${VERSION}" \
    --scripts "${scripts_dir}" \
    --install-location "/" \
    "${pkg_path}"

  shasum -a 256 "${pkg_path}" | awk '{ print tolower($1) " *" $2 }' > "${pkg_path}.sha256"
}

publish_runtime osx-x64
publish_runtime osx-arm64

release_notes_path="${REPO_ROOT}/${OUTPUT_ROOT}/output/macos-release-notes.md"
{
  echo "## macOS installers"
  echo
  echo "Built from GitHub or local automation on ${TIMESTAMP_UTC}."
  echo
  for artifact in "${REPO_ROOT}/${OUTPUT_ROOT}/output"/*.pkg; do
    checksum_file="${artifact}.sha256"
    checksum="$(cut -d ' ' -f 1 < "${checksum_file}")"
    echo "- $(basename "${artifact}")"
    echo "- SHA-256: ${checksum}"
  done
  echo
  echo "Signing status: unsigned unless a later workflow step re-signs the packages with a Developer ID Installer certificate."
} > "${release_notes_path}"

echo "Built macOS packages in ${REPO_ROOT}/${OUTPUT_ROOT}/output"