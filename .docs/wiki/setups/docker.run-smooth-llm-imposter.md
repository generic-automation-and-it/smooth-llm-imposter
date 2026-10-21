# Docker / Podman → run SmoothLlmImposter in a container

## TL;DR

Run the published multi-architecture image on `localhost:5080`, or build the Host image **from source**.
The [`Dockerfile`](../../../Dockerfile) publishes .NET 10 NativeAOT into a non-root `runtime-deps` image
listening on `5080`. Routing is driven by the **`Imposter`** configuration, as with `dotnet run`.

```bash
docker pull ghcr.io/generic-automation-and-it/smooth-llm-imposter:latest
docker run -d --name smooth-llm-imposter --restart unless-stopped \
  -p 5080:5080 \
  ghcr.io/generic-automation-and-it/smooth-llm-imposter:latest
```

Docker selects the native image architecture. Do not force `linux/amd64` on Apple Silicon: the NativeAOT
executable should run natively on `linux/arm64`. When replacing a container, complete the pull before
removing the existing service. Verify `/health` using the command below; container state alone is insufficient.
For persisted Copilot connections, keep the existing volume and database path when recreating the container;
see [GitHub Copilot setup](github-copilot.configure-smooth-llm-imposter.md).

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
`$OPENCODE_GO_API_KEY` / `$OPENROUTER_API_KEY`:

```bash
# Remove any existing container with the same name first.
docker rm -f smooth-llm-imposter 2>/dev/null || true

docker run -d --name smooth-llm-imposter \
  -p 5080:5080 \
  -e OPENCODE_GO_API_KEY \
  -e OPENROUTER_API_KEY \
  smooth-llm-imposter:local
```

`AuthScheme` (`ApiKey`|`Bearer`) selects the auth header and defaults by dialect when omitted (openai → Bearer,
anthropic → ApiKey). The conventional secret var **follows that scheme**: `<NAME>_API_KEY`,
`<NAME>_AUTH_TOKEN`, and `<NAME>_AUTHORIZATION_BEARER` all fill the provider `Secret`, but a `Bearer` provider
prefers `_AUTH_TOKEN` → `_AUTHORIZATION_BEARER` → `_API_KEY`, while an `ApiKey` provider prefers `_API_KEY` →
`_AUTH_TOKEN` → `_AUTHORIZATION_BEARER` (the off-scheme vars stay fallbacks) — flip the effective scheme for a
provider with `-e <NAME>_AUTH_SCHEME=ApiKey|Bearer`. If a provider's gateway wants the credential in a
non-standard header name, set `AuthHeader` (e.g.
`-e Imposter__Providers__<name>__AuthHeader=api-key`) to relocate it.

Podman is identical (`podman run -d --name … -p 5080:5080 -e … smooth-llm-imposter:local`).

> **Never bake keys into the image or commit them.** Pass them at `run` time with `-e`, or load a local env file
> with `--env-file ./imposter.env` (keep that file out of git). The image's build stage copies only `src/` and the
> central build files — no secrets are embedded.

### Optional knobs

- **`-e ASPNETCORE_URLS`** — override the bind address (default in the image: `http://+:5080`). If you change it,
  adjust the `-p` mapping to match.
- **`-e Imposter__Providers__<name>__Dialect` / `__BaseUrl` / `__To` / `__Caching`** — define or extend the
  routing table per provider, keyed by provider name (e.g. `opencode-go-openai`, `openrouter-openai`). In the
  container only `anthropic-default` / `openai-default` are shipped, so these vars **create** imposter providers
  rather than overriding shipped ones — supply at least `Dialect` and `BaseUrl` (the server root **without** a
  `/v1` path) for a new provider.
- **`-e Admin__ApiKey` / `-e Admin__OperatorApiKey` / `-e ConnectionStrings__ImposterDb`** — admin keys enable
  `/admin/credentials`; `ConnectionStrings__ImposterDb` is only needed for PostgreSQL persistence (reach it from
  the container, e.g. `Host=host.docker.internal;Port=5432;…`). Pure imposter routing needs no database; when
  unset credential-admin state is in memory. Full guide:
  [`credentials.admin-smooth-llm-imposter.md`](credentials.admin-smooth-llm-imposter.md).

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

Send a request — the shipped image carries **only** the two keyless default passthrough providers
(`anthropic-default`, `openai-default`) and no model rewrites; the secret-bearing imposter providers live in
`appsettings.Development.json`, which is **not** shipped in the image (excluded from publish, and Production never
loads it). To route specific inbound models to an alternate upstream in the container, define the imposter
provider via env (`Imposter__Providers__<name>__*`) or the `/admin/providers` runtime CRUD API. Example — creating
the `opencode-go-openai` provider from env (requires `OPENCODE_GO_API_KEY`, or the structured
`Imposter__Providers__opencode-go-openai__Secret`, plus its `Dialect`/`BaseUrl`):

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
  -e OPENROUTER_API_KEY \
  smooth-llm-imposter:local
```

Add `--no-cache` to `docker build` to force a clean rebuild if a cached layer is masking a change.

## Stop / remove

```bash
docker stop smooth-llm-imposter && docker rm smooth-llm-imposter
```
