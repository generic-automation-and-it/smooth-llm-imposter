---
description: 'Backend logging level conventions — default to Debug, reserve Information for operation boundaries'
globs: "**/*.cs"
paths:
  - "**/*.cs"
applyTo: '**/*.cs'
alwaysApply: false
---

# Backend Logging Conventions

Updated: 2026-04-03

## Default Log Levels

| Level | When to use |
|-------|-------------|
| `LogInformation` | **Operation start and end only** — e.g. "VEM sync started", "VEM sync completed. Agents processed: 5" |
| `LogDebug` | Everything else — fetched counts, skip decisions, per-item processing, flow-disabled messages |
| `LogError` | Exceptions and failures that need attention |
| `LogWarning` | Degraded states that aren't failures (e.g. retries, fallbacks) |

## Rationale

Excessive `Information`-level logging creates noise in production. Debug-level messages are available when needed by adjusting the log level per namespace in Serilog configuration. This keeps default production output clean while preserving full observability when troubleshooting.

## Examples

```csharp
// Start of operation — Information
logger.LogInformation("VEM sync started");

// Mid-operation detail — Debug
logger.LogDebug("Fetched {AgentCount} agents from VEM", agents.Count);
logger.LogDebug("Compare version {Major}.{Minor} already recorded for agent {AgentId}", ...);

// End of operation — Information
logger.LogInformation("VEM sync completed. Agents processed: {AgentCount}", count);

// Failure — Error
logger.LogError(ex, "Compare flow failed for agent {AgentId}", info.AgentId);
```

## Changelog

| Date | Change |
|:-----|:-------|
| 2026-05-30 | Initial version. |
