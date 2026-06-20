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

Then configure the `Imposter` section and point your client's base URL at the router. Every run mode —
local, debug + `dotnet user-secrets`, Docker, GHCR image, Compose, and the Conductor fresh-sandbox — is
covered in [`.docs/wiki/setup.md`](setup.md) and the guides under [`.docs/wiki/setups/`](setups/).

## Test

```bash
dotnet test SmoothLlmImposter.slnx
```

Tests are infra-free (no DB, no containers); integration tests stub the upstream transport in-process. See
[`.docs/wiki/testing.md`](testing.md).
