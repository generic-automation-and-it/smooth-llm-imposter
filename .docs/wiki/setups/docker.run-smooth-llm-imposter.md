# Docker / Podman → run SmoothLlmImposter in a container

## TL;DR

Build the Host image **from source** and run it on `localhost:5080`. There is **no published registry image** —
this router is stateless and key-less, so you build the image yourself from the repo's [`Dockerfile`](../../../Dockerfile)
(multi-stage: .NET 10 SDK publish → `aspnet:10.0` runtime, non-root, listening on `5080`). Point any OpenAI- or
Anthropic-dialect client's base URL at `http://localhost:5080`; routing is driven by the **`Imposter`** config
section exactly as in a `dotnet run`.

> The `docker pull ghcr.io/...:latest` flow you may have seen belongs to the **Smooth Claude Proxy**, a different
> project. SmoothLlmImposter publishes no image; the steps below build one locally.

Docker and Podman are interchangeable here — every `docker` command works as `podman` with the same arguments.

## Prerequisites

- **Docker** (Desktop / Engine) **or Podman**. Nothing else — the .NET 10 SDK is supplied by the build stage, so
  you do **not** need it installed on the host.
- Upstream provider keys to `export` at run time (see below). The image starts without keys, but any routed
  request to a keyless provider fails upstream.

## Build the image

From the repository root (the build context must include `src/` and the central build files):

```bash
docker build -t smooth-llm-imposter:local .
# Podman:
podman build -t smooth-llm-imposter:local .
```

## Run it

Pass configuration via environment variables using the standard double-underscore path syntax. Map the
container's `5080` to a host port. The example below assumes your shell already exports
`$OPENCODE_GO_API_KEY` and `$OPENROUTER_API_KEY`:

```bash
# Remove any existing container with the same name first.
docker rm -f smooth-llm-imposter 2>/dev/null || true

docker run -d --name smooth-llm-imposter \
  -p 5080:5080 \
  -e OPENCODE_GO_API_KEY \
  -e OPENCODE_GO_AUTH_SCHEME="ApiKey" \
  -e OPENROUTER_API_KEY \
  -e OPENROUTER_AUTH_SCHEME="Bearer" \
  -e OPENCODE_ANTHROPIC_API_KEY="$OPENCODE_GO_API_KEY" \
  -e OPENCODE_ANTHROPIC_AUTH_SCHEME="ApiKey" \
  smooth-llm-imposter:local
```

`AuthScheme` (`ApiKey`|`Bearer`) selects the auth header and defaults by dialect when omitted (openai → Bearer,
anthropic → ApiKey); the shipped providers set it explicitly.

Podman is identical (`podman run -d --name … -p 5080:5080 -e … smooth-llm-imposter:local`).

> **Never bake keys into the image or commit them.** Pass them at `run` time with `-e`, or load a local env file
> with `--env-file ./imposter.env` (keep that file out of git). The image's build stage copies only `src/` and the
> central build files — no secrets are embedded.

### Optional knobs

- **`-e ASPNETCORE_URLS`** — override the bind address (default in the image: `http://+:5080`). If you change it,
  adjust the `-p` mapping to match.
- **`-e Imposter__Providers__<name>__BaseUrl` / `__To` / `__Caching`** — override the shipped `appsettings.json`
  routing table per provider, keyed by provider name (e.g. `opencode-go`, `openrouter`). `BaseUrl` is the server
  root **without** a `/v1` path.
- **`-e Admin__ApiKey` / `-e Admin__OperatorApiKey` / `-e ConnectionStrings__ImposterDb`** — only needed for the
  optional `/admin/credentials` API (requires PostgreSQL; reach it from the container, e.g.
  `Host=host.docker.internal;Port=5432;…`). Pure imposter routing needs no database.

> **Data Protection keys (only matters if you use the credential-admin API).** Stored credentials are encrypted at
> rest with ASP.NET Core Data Protection, whose keys default to a path **inside the container** — destroying the
> container makes previously-stored secrets unreadable (you'll see a `Storing keys in a directory … that may not be
> persisted` warning on startup). Persist them by mounting a volume:
> `-v slli-dpkeys:/home/app/.aspnet/DataProtection-Keys`. Pure imposter routing stores nothing, so the warning is
> harmless there.

## Verify

```bash
curl -fsS http://localhost:5080/health        # {"status":"ok"}
```

Send a routed request — with the shipped config, OpenAI `gpt-5.4` is rewritten to `kimi-k2.7` and forwarded to
opencode-go (requires `OPENCODE_GO_API_KEY`, or the structured `Imposter__Providers__opencode-go__Secret`):

```bash
curl -fsS http://localhost:5080/v1/chat/completions \
  -H "content-type: application/json" \
  -d '{ "model": "gpt-5.4", "messages": [ { "role": "user", "content": "Say hello in one sentence." } ] }'
```

Follow the container logs (Serilog writes to stdout):

```bash
docker logs -f smooth-llm-imposter
```

## Rebuild after code changes

The image is a point-in-time publish of `src/`. After changing code, rebuild and recreate the container:

```bash
docker rm -f smooth-llm-imposter
docker build -t smooth-llm-imposter:local .
docker run -d --name smooth-llm-imposter \
  -p 5080:5080 \
  -e OPENCODE_GO_API_KEY \
  -e OPENCODE_GO_AUTH_SCHEME="ApiKey" \
  -e OPENROUTER_API_KEY \
  -e OPENROUTER_AUTH_SCHEME="Bearer" \
  -e OPENCODE_ANTHROPIC_API_KEY="$OPENCODE_GO_API_KEY" \
  -e OPENCODE_ANTHROPIC_AUTH_SCHEME="ApiKey" \
  smooth-llm-imposter:local
```

Add `--no-cache` to `docker build` to force a clean rebuild if a cached layer is masking a change.

## Stop / remove

```bash
docker stop smooth-llm-imposter && docker rm smooth-llm-imposter
```
