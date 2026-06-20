# GHCR image → run the published SmoothLlmImposter container

## TL;DR

Pull the **published** Host image from the GitHub Container Registry and run it on `localhost:5080`. The image is
built and pushed by [`.github/workflows/publish-image.yml`](../../../.github/workflows/publish-image.yml) on every
push to `main` (tagged `:latest`) and on version tags (`v*` → semver tags). Routing is driven by the **`Imposter`**
config section, supplied via environment variables.

```
ghcr.io/generic-automation-and-it/smooth-llm-imposter:latest
```

> **This is the SmoothLlmImposter image — not the Smooth Claude Proxy.** They are different services with different
> config: this router is **stateless and key-less** (no `/data` volume, no `WORKSPACE_PATH`), uses port **5080**,
> and keys are `Imposter__Providers__N__ApiKey` — there is no `LlmService__*` / `OPENCODE_GO_API_KEY` / `LOG_TOKEN_FORMAT`.

## Prerequisites

- **Docker** or **Podman**.
- The package must be **public** (or you must `docker login ghcr.io` with a token that can read it). See
  [Make the package public](#make-the-package-public-one-time) — a one-time step after the first publish.

## Run it (Docker)

Create or replace the container from the published image:

```bash
docker rm -f smooth-llm-imposter >/dev/null 2>&1 || true
docker run -d --name smooth-llm-imposter --restart unless-stopped \
  -p 5080:5080 \
  -e Imposter__Providers__2__ApiKey \
  -e Imposter__Providers__3__ApiKey \
  -e Imposter__Providers__4__ApiKey \
  ghcr.io/generic-automation-and-it/smooth-llm-imposter:latest
```

`-e NAME` (no `=value`) **passes the variable through from your current shell** — so `export` your keys first and
they never appear in the command line or shell history:

```bash
export Imposter__Providers__2__ApiKey="sk-your-opencode-key"
export Imposter__Providers__3__ApiKey="sk-your-openrouter-key"
export Imposter__Providers__4__ApiKey="sk-your-anthropic-route-key"
```

After the container has been created once, start it again with:

```bash
docker start smooth-llm-imposter
```

## Run it (Podman)

Identical, with Podman's SELinux-aware flags where relevant (none needed here — there is no bind-mounted volume):

```bash
podman rm -f smooth-llm-imposter >/dev/null 2>&1 || true
podman run -d --name smooth-llm-imposter --restart unless-stopped \
  -p 5080:5080 \
  -e Imposter__Providers__2__ApiKey \
  -e Imposter__Providers__3__ApiKey \
  -e Imposter__Providers__4__ApiKey \
  ghcr.io/generic-automation-and-it/smooth-llm-imposter:latest
```

## Pin a version

`:latest` tracks `main`. For a reproducible deploy, pull a semver tag published from a `v*` git tag:

```bash
docker pull ghcr.io/generic-automation-and-it/smooth-llm-imposter:1.4.0
docker run -d --name smooth-llm-imposter --restart unless-stopped -p 5080:5080 \
  -e Imposter__Providers__2__ApiKey \
  ghcr.io/generic-automation-and-it/smooth-llm-imposter:1.4.0
```

## Optional knobs

- **`-e ASPNETCORE_URLS`** — override the bind address (image default `http://+:5080`); adjust `-p` to match.
- **`-e Imposter__Providers__N__To` / `__BaseUrl` / `__Caching`** — override the shipped `appsettings.json` routing
  table per provider/mapping at run time (model mappings are config, not flat env — there is no single
  `default_model` switch).
- **`-e Admin__ApiKey` / `-e ConnectionStrings__ImposterDb`** — only for the optional `/admin/credentials` API
  (needs PostgreSQL, e.g. `Host=host.docker.internal;Port=5432;…`). If you use it, persist Data Protection keys
  with `-v slli-dpkeys:/home/app/.aspnet/DataProtection-Keys` so stored secrets survive a rebuild. Pure imposter
  routing stores nothing.

## Verify

```bash
curl -fsS http://localhost:5080/health        # {"status":"ok"}
docker logs -f smooth-llm-imposter             # Serilog writes to stdout
```

## Make the package public (one-time)

The first push creates a **private** GHCR package. To allow `docker pull` without authenticating, an org owner sets
it public once: GitHub → the repo's org → **Packages** → `smooth-llm-imposter` → **Package settings** → **Change
visibility → Public**. (Alternatively keep it private and `docker login ghcr.io -u <user>` with a PAT that has
`read:packages`.)
