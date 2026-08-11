# Tarpit notes

This directory contains legacy Python tarpit utilities from the earlier stack evolution.

The supported tarpit implementation in this repository is now the .NET code path:

- Core gateway routing and tarpit exposure: [RedisBlocklistMiddlewareApp/Program.cs](../RedisBlocklistMiddlewareApp/Program.cs)
- Tarpit rendering and archive generation: [AiScrapingDefense.TarpitApi](../AiScrapingDefense.TarpitApi)
- Product overview: [README.md](../README.md)
- Current architecture: [docs/architecture.md](../docs/architecture.md)
- Tarpit strategy and parity notes: [docs/tarpit_content_strategy_baseline.md](../docs/tarpit_content_strategy_baseline.md)

Use the top-level .NET docs and projects above for current setup, deployment, and implementation guidance. The files in this folder are not the primary runtime path for the shipped IIS/.NET stack.
