# Admin UI notes

This directory contains legacy Python-era admin UI assets that were carried forward during the .NET transition.

For the current product surface, the active operator dashboard is the ASP.NET Core management UI exposed by the edge gateway under `/defense/dashboard`.

Current entry points:

- Main product overview: [README.md](../README.md)
- Operator and deployment docs: [docs/index.md](../docs/index.md)
- Runtime host and dashboard endpoints: [RedisBlocklistMiddlewareApp/Program.cs](../RedisBlocklistMiddlewareApp/Program.cs)
- Operator workflow guidance: [docs/operator_runbook.md](../docs/operator_runbook.md)

If you are looking for the supported management interface, start with the top-level docs above instead of the legacy Python files in this folder.