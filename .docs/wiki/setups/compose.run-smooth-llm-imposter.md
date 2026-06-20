# Compose → run SmoothLlmImposter with docker compose / podman-compose

## TL;DR

Bring the Host up on `localhost:5066` with one command using the repo's [`docker-compose.yml`](../../../docker-compose.yml). It
is **dual-mode**: `build: .` builds the image from the local [`Dockerfile`](../../../Dockerfile), and `image:`
points at the published GHCR tag — so you can either build locally or `pull`. `restart: unless-stopped` keeps it
running across reboots. Works with **`docker compose`** (v2) and **`podman-compose`**.

> SmoothLlmImposter is **stateless and key-less** — no `/data` volume, port **5066**, keys are `<NAME>_API_KEY`
> (conventional) or `Imposter__Providers__<name>__Secret` (structured) plus the matching `<NAME>_AUTH_SCHEME` /
> `Imposter__Providers__<name>__AuthScheme`, where `<NAME>` is the uppercased provider key. (The Smooth Claude
> Proxy's compose, with `WORKSPACE_PATH`/`LlmService__*`, is a different service.)

## Supply keys

Compose auto-loads a `./.env` file (gitignored via `*.env`) for `${...}` interpolation, or reads exported shell
variables. Create `.env` next to `docker-compose.yml`:

```dotenv
# .env  (never committed — *.env is gitignored)
OPENCODE_GO_API_KEY=sk-your-opencode-key              # feeds opencode-go-openai and opencode-go-anthropic
OPENROUTER_API_KEY=sk-your-openrouter-key             # feeds openrouter-openai and openrouter-anthropic
```

`docker-compose.yml` maps these named variables onto the name-keyed
`Imposter__Providers__<name>__Secret` settings (or the conventional `<NAME>_API_KEY` surface) — edit the
`environment:` block there to match your provider names. Overrides are keyed by provider name and survive
reordering, so there is no positional `__N__` addressing. An unknown provider name simply creates a new provider
entry, and a legacy numeric/array config is rejected at startup with a message naming the
`Providers: { "<name>": { ... } }` object shape.

## Build & first run (local dockerized testing)

The image must be built before it can run. `podman-compose up -d` auto-builds (the compose file has
`build: .`), but building explicitly first makes any build failure obvious instead of silently skipping the
start. Full first-run sequence:

```bash
# 0) Be in the repo root — the folder that contains docker-compose.yml
cd /path/to/smooth-llm-imposter        # your conductor workspace dir
ls docker-compose.yml                  # sanity check: should print the filename

#    Shut down + remove any existing smooth-llm-imposter container first, so a
#    stale one can't hold the name or the port. Both lines are safe no-ops if
#    nothing is running. (docker: swap podman-compose->docker compose, podman->docker)
podman-compose down 2>/dev/null || true
podman rm -f smooth-llm-imposter 2>/dev/null || true

# 1) (optional) upstream keys — only needed for ROUTED calls, NOT for /health.
#    Pulls the values from your host shell (export them first), so no secrets are
#    hardcoded; *.env is gitignored. Note the UNQUOTED <<EOF — that is what lets
#    ${...} expand. Skip this step if you just want to test /health.
cat > .env <<EOF
OPENCODE_GO_API_KEY=${OPENCODE_GO_API_KEY:-}
OPENROUTER_API_KEY=${OPENROUTER_API_KEY:-}
EOF

# 2) Build the image from the Dockerfile
podman-compose build

# 3) FIRST run in the foreground so you SEE the startup logs / any error live
podman-compose up
#    -> watch for Serilog "Now listening on: http://[::]:5066"
#    -> if it boots OK, press Ctrl-C to stop, then start detached:
podman-compose up -d

# 4) Confirm it's actually running
podman ps                              # should list smooth-llm-imposter, ports 5066->5066
podman logs smooth-llm-imposter        # startup logs

# 5) Verify from the host
curl -fsS http://localhost:5066/health     # {"status":"ok"}

# 6) Stop when done
podman-compose down                    # stop + remove
# or keep it for a quick restart:
podman-compose stop  &&  podman-compose start
```

> Running step 3 in the **foreground** is the key diagnostic: if the container "vanishes" (`podman-compose ps`
> reports *no container found*), the foreground run shows whether it boots and listens on `:5066` or crashes
> with a startup/config error — right in your terminal. `/health` needs **no keys**; test it first to confirm
> the container runs, then supply keys for routed `/v1/...` calls.
>
> If `podman ps` lists the container under a project-prefixed name (e.g. `…_smooth-llm-imposter_1`) rather than
> plain `smooth-llm-imposter`, that is a `podman-compose` + `container_name:` quirk — reference the prefixed
> name, or drop `container_name:` from `docker-compose.yml`.

The subsections below break out the individual lifecycle commands (start/stop without rebuild, full rebuild,
verify).

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

## Local run — start / stop / restart (no rebuild)

For day-to-day local testing once the image is built, you don't need the full rebuild. Use `stop`/`start` to
keep the container around (fastest), or `down`/`up` to recreate it:

```bash
# Stop but KEEP the container — fastest pause/resume
podman-compose stop
podman-compose start

# Restart in place
podman-compose restart

# Stop and REMOVE container + network, then recreate
podman-compose down
podman-compose up -d

# Follow logs / check status
podman-compose logs -f
podman-compose ps
```

`docker compose` (v2) accepts the same subcommands. Only reach for the **Full rebuild** below when a code change
isn't being picked up.

> **`EXPOSE 5080` in the build output is expected and harmless.** It's static metadata from the `Dockerfile`
> and does **not** set the published port. The compose file binds the app to **5066** (`ASPNETCORE_URLS:
> http://+:5066`, which overrides the image's default) and publishes `5066:5066`, so the router is reachable at
> `http://localhost:5066` regardless of the `EXPOSE` line.

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
curl -fsS http://localhost:5066/health        # {"status":"ok"}
docker compose logs -f                         # podman-compose logs -f
```

Point Codex and Anthropic-dialect clients at the Compose port **plus the dialect prefix** before sending
requests through the router. The router selects the dialect from the `/openai` or `/anthropic` prefix and
forwards the rest of the path verbatim:

For the OpenAI default passthrough provider in `appsettings.json`, set `BaseUrl` based on the auth path:

- API-key/OpenAI Platform passthrough: `"BaseUrl": "https://api.openai.com"`
- Codex CLI ChatGPT/subscription passthrough: `"BaseUrl": "https://chatgpt.com/backend-api/codex"`

```toml
# ~/.codex/config.toml — Codex CLI with ChatGPT/subscription auth
model_provider = "smooth-llm-proxy"

[model_providers.smooth-llm-proxy]
name = "Smooth LLM Imposter"
base_url = "http://127.0.0.1:5066/openai"
wire_api = "responses"
requires_openai_auth = true
```

That provider selection applies to every local Codex model request for the selected config/profile, including
models selected later with `/model`. It does not proxy Codex login, model-catalog refresh, web search, MCP
servers, connectors, or cloud tasks.

For generic OpenAI-compatible SDK/API-key clients, keep `/v1` in the base URL because those clients append bare
paths like `/responses`, `/chat/completions`, and `/models`:

```toml
openai_base_url = "http://localhost:5066/openai/v1"
```

```bash
# Anthropic SDK appends /v1/messages itself, so NO /v1 here
export ANTHROPIC_BASE_URL="http://localhost:5066/anthropic"
```

For Claude subscription-based upstreams, create a token with the Claude CLI and supply it yourself as the
imposter provider `Secret` override:

```bash
claude setup-token

# Example: openrouter-anthropic is the shipped Anthropic-dialect imposter path.
# It shares the same OpenRouter key as the OpenAI-dialect openrouter-openai provider.
export OPENROUTER_API_KEY="paste-the-openrouter-key-here"
```

Use `AuthScheme="Bearer"` when the upstream expects `Authorization: Bearer <token>`. Use `AuthScheme="ApiKey"`
when the upstream expects `x-api-key: <token>`.

Send a routed request — with the shipped config, OpenAI `gpt-5.4` is rewritten to `kimi-k2.7` and forwarded
to opencode-go-openai (requires the provider's `Secret`):

```bash
curl -fsS http://localhost:5066/openai/v1/chat/completions \
  -H "content-type: application/json" \
  -d '{ "model": "gpt-5.4", "messages": [ { "role": "user", "content": "Say hello in one sentence." } ] }'
```

## Credential-admin API (optional)

If you use `/admin/credentials` (needs PostgreSQL), uncomment the `volumes:` block in `docker-compose.yml` to persist
Data Protection keys (`slli-dpkeys:/home/app/.aspnet/DataProtection-Keys`) so encrypted secrets survive a rebuild,
and add `Admin__ApiKey` / `ConnectionStrings__ImposterDb` to your `.env`. Pure imposter routing needs neither.
