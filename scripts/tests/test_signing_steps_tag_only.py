import re
import unittest
from pathlib import Path

WORKFLOWS = Path(__file__).resolve().parents[2] / ".github" / "workflows"


def steps(text):
    return re.split(r"(?m)^      - ", text)[1:]


class SigningSecretsAreTagOnly(unittest.TestCase):
    def test_installer_steps_using_signing_secrets_run_only_on_tags(self):
        checked = 0
        for name in ("windows-installer.yml", "macos-installer.yml"):
            for step in steps((WORKFLOWS / name).read_text()):
                if re.search(r"secrets\.(WINDOWS_SIGN|MACOS_)", step):
                    checked += 1
                    self.assertRegex(
                        step,
                        r"(?m)^\s+if: github\.ref_type == 'tag'\s*$",
                        f"{name}: step uses signing secrets without a tag-only guard:\n{step[:120]}",
                    )
        self.assertGreaterEqual(checked, 4)


if __name__ == "__main__":
    unittest.main()
