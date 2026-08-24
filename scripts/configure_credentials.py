#!/usr/bin/env python3
"""Interactively configure credentials for the local IIS/.NET stack."""

from __future__ import annotations

import getpass
import secrets
from pathlib import Path

KEYS = (
    "ASD_SPLIT_SERVICE_TOKEN",
    "ASD_MANAGEMENT_API_KEY",
    "ASD_INTAKE_API_KEY",
    "ASD_POSTGRES_PASSWORD",
    "ASD_REDIS_PASSWORD",
)


def main() -> None:
    root = Path(__file__).resolve().parent.parent
    target = root / ".env"
    sample = root / "compose.env.example"
    if not target.exists():
        target.write_text(sample.read_text(encoding="utf-8"), encoding="utf-8")
    lines = target.read_text(encoding="utf-8").splitlines(keepends=True)
    existing: dict[str, str] = {}
    for line in lines:
        if line and not line.lstrip().startswith("#") and "=" in line:
            key, value = line.rstrip("\n").split("=", 1)
            existing[key] = value
    updates: dict[str, str] = {}
    print("Leave a prompt blank to generate a strong random value.")
    for key in KEYS:
        current = existing.get(key, "")
        is_placeholder = current.startswith("replace-with-")
        keepable = current if len(current) >= 32 and not is_placeholder else ""
        while True:
            entered = getpass.getpass(
                f"{key} [{'keep existing' if keepable else 'generate'}]: "
            ).strip()
            value = entered or keepable or secrets.token_urlsafe(36)
            if len(value) >= 32:
                updates[key] = value
                break
            print(f"{key} must contain at least 32 characters.")
    mcp_enabled_by_default = existing.get("MODEL_URI", "").startswith("mcp://")
    default_choice = "Y/n" if mcp_enabled_by_default else "y/N"
    configure_mcp = (
        input(f"Configure an MCP classifier? [{default_choice}]: ").strip().lower()
    )
    mcp_enabled = (
        mcp_enabled_by_default if not configure_mcp else configure_mcp in {"y", "yes"}
    )
    if mcp_enabled:
        updates["MODEL_URI"] = "mcp://primary/classify"
        current_url = existing.get(
            "MCP_SERVER_PRIMARY_URL", "ws://host.docker.internal:8085/mcp"
        )
        updates["MCP_SERVER_PRIMARY_URL"] = (
            input(f"MCP WebSocket URL [{current_url}]: ").strip() or current_url
        )
        current_token = existing.get("MCP_SERVER_PRIMARY_AUTH_TOKEN", "")
        token = getpass.getpass(
            f"MCP bearer token [{'keep existing' if current_token else 'generate'}]: "
        ).strip()
        updates["MCP_SERVER_PRIMARY_AUTH_TOKEN"] = (
            token or current_token or secrets.token_urlsafe(36)
        )
        current_timeout = existing.get("MCP_SERVER_PRIMARY_TIMEOUT", "10")
        updates["MCP_SERVER_PRIMARY_TIMEOUT"] = (
            input(f"MCP timeout in seconds [{current_timeout}]: ").strip()
            or current_timeout
        )
    else:
        updates["MODEL_URI"] = ""
    output: list[str] = []
    remaining = dict(updates)
    for line in lines:
        key = (
            line.split("=", 1)[0]
            if "=" in line and not line.lstrip().startswith("#")
            else None
        )
        if key in remaining:
            output.append(f"{key}={remaining.pop(key)}\n")
        else:
            output.append(line)
    output.extend(f"{key}={value}\n" for key, value in remaining.items())
    target.write_text("".join(output), encoding="utf-8")
    target.chmod(0o600)
    print(f"Credentials written to {target}. Run: docker compose up --build")


if __name__ == "__main__":
    main()
