import re
import unittest
from pathlib import Path

WORKFLOWS = Path(__file__).resolve().parents[2] / ".github" / "workflows"


def jobs(text):
    parts = re.split(r"(?m)^  ([a-z0-9-]+):\s*$", text)
    return {parts[i]: parts[i + 1] for i in range(1, len(parts), 2)}


class InstallerSigningIsolationTests(unittest.TestCase):
    def test_build_and_publish_jobs_never_reference_signing_secrets(self):
        for name in ("windows-installer.yml", "macos-installer.yml"):
            parsed = jobs((WORKFLOWS / name).read_text())
            self.assertIn("build-installer", parsed, name)
            self.assertIn("sign-installer", parsed, name)
            self.assertIn("publish-installer", parsed, name)

            build = parsed["build-installer"]
            self.assertRegex(build, r"(?m)^    permissions:\s*\n\s+contents: read\s*$")
            self.assertNotRegex(build, r"secrets\.(WINDOWS_SIGN|MACOS_)")

            publish = parsed["publish-installer"]
            self.assertRegex(publish, r"(?m)^    if:.*github\.ref_type == 'tag'")
            self.assertNotRegex(publish, r"secrets\.(WINDOWS_SIGN|MACOS_)")

    def test_sign_job_requires_tag_signing_enabled_environment_and_digest_check(self):
        for name in ("windows-installer.yml", "macos-installer.yml"):
            sign = jobs((WORKFLOWS / name).read_text())["sign-installer"]
            self.assertRegex(
                sign,
                r"(?m)^    if: github\.ref_type == 'tag' && vars\.SIGNING_ENABLED == 'true'\s*$",
                name,
            )
            self.assertRegex(sign, r"(?m)^    environment: release-signing\s*$", name)
            self.assertRegex(sign, r"(?m)^    permissions:\s*\n\s+contents: read\s*$", name)
            self.assertIn("Verify Unsigned", sign, name)
            self.assertRegex(sign, r"secrets\.(WINDOWS_SIGN|MACOS_)", name)


if __name__ == "__main__":
    unittest.main()
