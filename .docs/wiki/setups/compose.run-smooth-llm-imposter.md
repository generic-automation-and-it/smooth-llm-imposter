# Compose → run SmoothLlmImposter with docker compose / podman-compose

## TL;DR

Bring the Host up on `localhost:5080` with one command using the repo's [`compose.yaml`](../../../compose.yaml). It
is **dual-mode**: `build: .` builds the image from the local [`Dockerfile`](../../../Dockerfile), and `image:`
points at the published GHCR tag — so you can either build locally or `pull`. `restart: unless-stopped` keeps it
running across reboots. Works with **`docker compose`** (v2) and **`podman-compose`**.

> SmoothLlmImposter is **stateless and key-less** — no `/data` volume, port **5080**, keys are
> `Imposter__Providers__N__ApiKey`. (The Smooth Claude Proxy's compose, with `WORKSPACE_PATH`/`LlmService__*`, is a
> different service.)

## Supply keys

Compose auto-loads a `./.env` file (gitignored via `*.env`) for `${...}` interpolation, or reads exported shell
variables. Create `.env` next to `compose.yaml`:

```dotenv
# .env  (never committed — *.env is gitignored)
Imposter__Providers__0__ApiKey=sk-your-opencode-key
Imposter__Providers__1__ApiKey=sk-your-anthropic-route-key
```

## Up / down

```bash
# Build (if needed) and start, detached
docker compose up -d
# Podman:
podman-compose up -d

# Stop and remove the container/network
docker compose down
podman-compose down
```

Pull the published GHCR image instead of building locally:

```bash
docker compose pull && docker compose up -d
```

## Full rebuild (use when code changes aren't being picked up)

Tear down, drop the stale local image, rebuild without cache, and start fresh:

```bash
# Docker
docker compose down \
  && docker image rm ghcr.io/generic-automation-and-it/smooth-llm-imposter:latest --force 2>/dev/null; \
  docker compose build --no-cache \
  && docker compose up -d

# Podman
podman-compose down \
  && podman rmi ghcr.io/generic-automation-and-it/smooth-llm-imposter:latest --force 2>/dev/null; \
  podman-compose build --no-cache \
  && podman-compose up -d
```

## Verify

```bash
curl -fsS http://localhost:5080/health        # {"status":"ok"}
docker compose logs -f                         # podman-compose logs -f
```

Send a routed request — with the shipped config, OpenAI `gpt5.4` is rewritten to `kimi-k2.7` and forwarded to
opencode-go (requires `Imposter__Providers__0__ApiKey`):

```bash
curl -fsS http://localhost:5080/v1/chat/completions \
  -H "content-type: application/json" \
  -d '{ "model": "gpt5.4", "messages": [ { "role": "user", "content": "Say hello in one sentence." } ] }'
```

## Credential-admin API (optional)

If you use `/admin/credentials` (needs PostgreSQL), uncomment the `volumes:` block in `compose.yaml` to persist
Data Protection keys (`slli-dpkeys:/home/app/.aspnet/DataProtection-Keys`) so encrypted secrets survive a rebuild,
and add `Admin__ApiKey` / `ConnectionStrings__ImposterDb` to your `.env`. Pure imposter routing needs neither.
