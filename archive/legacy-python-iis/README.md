# Legacy Python/IIS implementation (archived)

This directory preserves the original Python services and IIS reverse-proxy
configuration for historical reference. The supported runtime is the .NET
implementation at the repository root. Nothing in this directory is restored,
built, tested, packaged, or deployed by the current solution.

## Provenance

- Archived on 2026-08-11 from repository commit
  `95056f227ccb6606b3804f1b01f3da841fe50cb2`.
- The IIS configuration and PostgreSQL corpus importer date to the initial
  `5edd04b` implementation; the service documentation was last refreshed in
  `24c0cb2`.
- Git rename history preserves line-level attribution and earlier commits.

The dependencies and security assumptions in this tree are frozen. Do not run
it on an Internet-facing host without a fresh dependency, configuration, and
threat-model review.

## Contents

- `admin_ui`, `ai_service`, `escalation`, and `tarpit`: legacy web services
- `rag`: legacy training, fine-tuning, scanning, and corpus import utilities
- `shared` and `metrics.py`: shared Python runtime support
- `iis_configs`: IIS/FastCGI reverse-proxy examples for the Python services
- `requirements.txt`: the frozen legacy dependency list

## Restoration

For an isolated checkout in the original layout, use a worktree at the
pre-archive commit:

```powershell
git worktree add ..\ai-scraping-defense-iis-legacy 95056f227ccb6606b3804f1b01f3da841fe50cb2
```

This does not alter the active .NET checkout. To revive the implementation,
create a branch from that worktree and perform the security/dependency review
there; do not copy the archived files into the supported runtime implicitly.
