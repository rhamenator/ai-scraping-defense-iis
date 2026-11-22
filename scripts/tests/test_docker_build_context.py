"""The Docker build must not be able to copy credentials into an image layer (#194)."""

import os
import re
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PUBLISHED = (
    "RedisBlocklistMiddlewareApp/RedisBlocklistMiddlewareApp.csproj",
    "AiScrapingDefense.EscalationEngine/AiScrapingDefense.EscalationEngine.csproj",
    "AiScrapingDefense.TarpitApi/AiScrapingDefense.TarpitApi.csproj",
)
ROOT_FILES = {"Directory.Build.props", "global.json"}


def _dockerignore() -> set[str]:
    lines = (ROOT / ".dockerignore").read_text().splitlines()
    return {line.strip() for line in lines if line.strip() and not line.startswith("#")}


def _copies() -> list[tuple[list[str], str]]:
    """(sources, destination) of every COPY instruction that copies from the build context."""
    copies = []
    for line in (ROOT / "Dockerfile").read_text().splitlines():
        match = re.match(r"^\s*COPY\s+(.*)$", line)
        if not match or "--from=" in match.group(1):
            continue
        parts = match.group(1).split()
        copies.append((parts[:-1], parts[-1]))
    return copies


def _project_closure(project: str) -> set[str]:
    """Directories of a project and every project it references, transitively."""
    seen: set[str] = set()
    pending = [project]
    while pending:
        current = pending.pop()
        directory = str(Path(current).parent)
        if directory in seen:
            continue
        seen.add(directory)
        tree = ET.parse(ROOT / current)
        for reference in tree.iter("ProjectReference"):
            include = reference.attrib["Include"].replace("\\", "/")
            pending.append(os.path.normpath(os.path.join(os.path.dirname(current), include)))
    return seen


class DockerBuildContextTests(unittest.TestCase):
    def test_credential_files_are_excluded_from_the_context(self):
        ignored = _dockerignore()
        for pattern in (".env", ".env.*", "**/.env", "**/.env.*", "**/*.pem", "**/*.key", "**/*.pfx", "secrets"):
            self.assertIn(pattern, ignored)

    def test_no_instruction_copies_the_whole_context(self):
        for sources, _destination in _copies():
            for source in sources:
                self.assertNotIn(source.rstrip("/"), (".", "*"), "COPY . pulls every file in the context into the image")

    def test_only_known_files_and_project_folders_are_copied(self):
        closure = set()
        for project in PUBLISHED:
            closure |= _project_closure(project)
        allowed_dirs = {directory.rstrip("/") for directory in closure}
        for sources, _destination in _copies():
            for source in sources:
                if source in ROOT_FILES:
                    continue
                folder = source.split("/", 1)[0]
                self.assertIn(folder, allowed_dirs, f"COPY {source} is outside the published projects")

    def test_every_project_the_published_apps_need_is_copied(self):
        needed = set()
        for project in PUBLISHED:
            needed |= _project_closure(project)
        self.assertEqual(
            needed,
            {"RedisBlocklistMiddlewareApp", "AiScrapingDefense.EscalationEngine", "AiScrapingDefense.TarpitApi", "AiScrapingDefense.Contracts"},
        )
        copied_folders = {src.split("/", 1)[0] for sources, _ in _copies() for src in sources}
        self.assertTrue(needed <= copied_folders, f"missing from the Dockerfile: {needed - copied_folders}")
        copied_files = {src for sources, _ in _copies() for src in sources}
        self.assertTrue(ROOT_FILES <= copied_files)

    def test_restore_runs_before_the_sources_are_copied(self):
        text = (ROOT / "Dockerfile").read_text()
        self.assertLess(text.index("dotnet restore"), text.index("COPY AiScrapingDefense.Contracts/ "))

    def test_the_credential_script_still_writes_the_file_the_context_excludes(self):
        """If the script ever writes somewhere else, this guard needs to follow it."""
        script = (ROOT / "scripts" / "configure_credentials.py").read_text()
        self.assertIn('root / ".env"', script)
        self.assertIn(".env", _dockerignore())


if __name__ == "__main__":
    unittest.main()
