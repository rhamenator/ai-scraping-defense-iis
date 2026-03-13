# README for RedisBlocklistMiddlewareApp

This application is now the first .NET-native defense service in the repository. It is no longer treated as an IIS-only shim around Python services.

## Purpose

The app currently combines several roles that exist as separate services in the upstream Python stack:

1. Edge blocklist enforcement
2. Suspicious-request intake
3. Basic queued escalation analysis
4. Tarpit response generation
5. Recent defense event reporting

This is intentionally a foundation step. The current implementation keeps those roles in one ASP.NET Core application so the .NET contracts and request flow can settle before the solution is split into multiple services.

## Main Components

- `Program.cs`
  Registers configuration, Redis, background services, the middleware pipeline, and the tarpit plus health endpoints.
- `RedisBlocklistMiddleware.cs`
  Performs edge filtering, queues suspicious traffic, and rewrites suspicious requests into the tarpit route.
- `Configuration/DefenseEngineOptions.cs`
  Strongly typed configuration for Redis, heuristics, queueing, and tarpit behavior.
- `Services/RedisBlocklistService.cs`
  Reads and writes the Redis-backed IP blocklist.
- `Services/RedisRequestFrequencyTracker.cs`
  Tracks short-window suspicious request frequency per IP.
- `Services/DefenseAnalysisService.cs`
  Consumes queued suspicious requests, scores them, and blocks high-risk IPs.
- `Services/TarpitPageService.cs`
  Generates deterministic synthetic tarpit HTML.

## Current Endpoints

- `GET /`
- `GET /health`
- `GET /defense/events`
- `GET /anti-scrape-tarpit/{path}`

## Current Workflow

1. Incoming traffic enters the ASP.NET Core middleware pipeline.
2. Existing Redis blocklist hits are rejected immediately.
3. Known bad user agents are blocklisted and rejected immediately.
4. Suspicious requests are queued for analysis and rewritten to the tarpit endpoint.
5. The background analysis worker updates Redis frequency counters and can promote high-risk IPs into the blocklist.
6. Recent decisions are available through `/defense/events`.

## Direction

The next planned step is to split the current modular monolith into clearer .NET service boundaries that line up with the upstream system: edge gateway, AI intake, escalation engine, and tarpit API.
