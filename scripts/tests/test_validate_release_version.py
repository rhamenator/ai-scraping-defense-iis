import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from validate_release_version import version_from_tag  # noqa: E402


class ValidateReleaseVersion(unittest.TestCase):
    def test_accepts_semver(self):
        for tag, expected in (
            ("v1.1.4", "1.1.4"),
            ("v1.0.0-rc.1", "1.0.0-rc.1"),
            ("v2.3.4+build.5", "2.3.4+build.5"),
        ):
            self.assertEqual(version_from_tag(tag), expected)

    def test_rejects_shell_metacharacters_and_malformed_tags(self):
        for tag in (
            '1.2.3',
            'v1.2',
            'v01.2.3',
            'v1.2.3"; calc; "',
            "v1.2.3`id`",
            "v1.2.3$(id)",
            "v1.2.3 -Runtime x",
            "v1.2.3\nGITHUB_ENV=x",
            "v1.2.3;rm",
            "v1.2.3-",
            "v" + "1." * 80 + "2.3",
        ):
            with self.assertRaises(ValueError, msg=tag):
                version_from_tag(tag)


if __name__ == "__main__":
    unittest.main()
