# Developers — Getting Started

Build, run, and test SmoothLlmImposter from source. For running the router in a packaged or
deployed form (Docker, GHCR image, Compose) see the run-mode guides under
[`.docs/wiki/setups/`](setups/) and the master [`.docs/wiki/setup.md`](setup.md).

## Prerequisites

- **.NET 10 SDK**
- *(Optional)* A container runtime — Docker or Podman — for the image / Compose run modes.
- *(Optional)* **PostgreSQL** — only for the optional `/admin/credentials` passthrough-credential API. Core
  imposter routing needs no database.

## Build & run

```bash
dotnet build SmoothLlmImposter.slnx
dotnet run --project src/SmoothLlmImposter.Host        # -> http://localhost:5080
curl http://localhost:5080/health                      # {"status":"ok"}
```

Then configure the `Imposter` section and point your client's base URL at the router.

## Run modes & deeper guides

The guides below carry the developer-facing detail (debugger, dev secrets, building a local image, iterating
on source). They live under [`setups/`](setups/) so the run-mode index in [`setup.md`](setup.md) stays the
single source of truth — this page just links the ones a developer reaches for.

| Task | Guide |
|---|---|
| Run from source with a debugger + dev secrets | [`setups/local-debug.run-…`](setups/local-debug.run-smooth-llm-imposter.md) |
| Build & run a local image (Docker / Podman) | [`setups/docker.run-…`](setups/docker.run-smooth-llm-imposter.md) |
| One-command up/down + rebuild (Compose) | [`setups/compose.run-…`](setups/compose.run-smooth-llm-imposter.md) |
| Dump the full inbound request (Debug logging) | [`setups/logging.debug-…`](setups/logging.debug-smooth-llm-imposter.md) |

All run modes — including the GHCR published image and the Conductor fresh-sandbox — are indexed in
[`setup.md`](setup.md).

## Test

```bash
dotnet test SmoothLlmImposter.slnx
```

Tests are infra-free (no DB, no containers); integration tests stub the upstream transport in-process. See
[`.docs/wiki/testing.md`](testing.md).
