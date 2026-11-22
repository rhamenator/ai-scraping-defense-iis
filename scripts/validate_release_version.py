#!/usr/bin/env python3
"""Validate a release tag against the exact SemVer 2.0 grammar before CI uses it.

Usage: validate_release_version.py <tag>   (a leading "v" is required)
Prints the version without the "v" on success; exits 1 otherwise.
Workflows call this and pass the result on through environment variables,
never by interpolating it into shell source.
"""

from __future__ import annotations

import re
import sys

SEMVER = re.compile(
    r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
    r"(-((0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)"
    r"(\.(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?"
    r"(\+([0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*))?"
)


def version_from_tag(tag: str) -> str:
    if not tag.startswith("v"):
        raise ValueError("release tags must start with 'v'")
    version = tag[1:]
    if len(version) > 128 or SEMVER.fullmatch(version) is None:
        raise ValueError("release tag is not a valid semantic version")
    return version


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print("usage: validate_release_version.py <tag>", file=sys.stderr)
        return 2
    try:
        print(version_from_tag(argv[1]))
    except ValueError as exc:
        print(f"Invalid release tag: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
