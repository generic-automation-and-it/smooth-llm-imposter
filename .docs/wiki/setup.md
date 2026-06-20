# Setup

> **What this is.** SmoothLlmImposter is a stateless, key-less LLM request router. It exposes OpenAI- and
> Anthropic-dialect endpoints, reads the inbound `model`, and either rewrites it to a configured upstream
> ("imposter") — optionally injecting prompt caching — or passes it through. Keys come from config/env only;
> nothing about a request is persisted. There is **no `claude login` and no token capture** — you run the Host with
> `dotnet`, pull the published GHCR image, or build a local image from the repo
> [`Dockerfile`](../../Dockerfile), and point your existing OpenAI/Anthropic client's base URL at it.

## Setups

Pick the setup that matches how you want to run the router. Each links to a self-contained guide under
[`setups/`](setups/); the quick `dotnet` build & run below covers the common case.

| Setup | Description | Doc |
|---|---|---|
| **Local (`dotnet run`)** | Quickest start — build and run the Host on `:5080` with the SDK. Covered inline under [Build & run](#build--run) below. | _(this doc)_ |
| **Local debug + dev secrets** | Run from source with a debugger (VS / Rider / VS Code), launch profiles, and `dotnet user-secrets` for keys. | [`setups/local-debug.run-smooth-llm-imposter.md`](setups/local-debug.run-smooth-llm-imposter.md) |
| **Docker / Podman (local build)** | Build the Host image from the repo `Dockerfile` and run it in a container on `:5080`. | [`setups/docker.run-smooth-llm-imposter.md`](setups/docker.run-smooth-llm-imposter.md) |
| **GHCR published image** | Pull the prebuilt image (`ghcr.io/…/smooth-llm-imposter`, published on `main` + `v*` tags) and run it with `--restart unless-stopped`. | [`setups/ghcr.run-smooth-llm-imposter.md`](setups/ghcr.run-smooth-llm-imposter.md) |
| **Compose** | One-command up/down (and full rebuild) via `docker compose` / `podman-compose` from `docker-compose.yml`. | [`setups/compose.run-smooth-llm-imposter.md`](setups/compose.run-smooth-llm-imposter.md) |
| **Conductor.Build fresh-sandbox** | Install .NET, build & run the Host on `:5080`, and point a client at it. | [`setups/conductor.build-smooth-llm-imposter.md`](setups/conductor.build-smooth-llm-imposter.md) |

## Prerequisites

- **.NET 10 SDK**
- **PostgreSQL** — only if you use the `/admin/credentials` API or stored passthrough-credential overrides. Pure
  imposter routing (model rewrite + caching + passthrough to a config-keyed provider) needs no database.

## Build & run

The Host listens on **`http://localhost:5080`** (and `https://localhost:7080`) under the launch profile:

```bash
dotnet build SmoothLlmImposter.slnx
dotnet run --project src/SmoothLlmImposter.Host        # -> http://localhost:5080
```

For a self-contained run (published binary, no launch profile), bind the port explicitly:

```bash
dotnet publish src/SmoothLlmImposter.Host -c Release -o ./publish
ASPNETCORE_URLS=http://+:5080 ./publish/SmoothLlmImposter.Host
```

Verify it is up:

```bash
curl http://localhost:5080/health        # {"status":"ok"}
```

## Configure routing

Routing is driven entirely by the **`Imposter`** configuration section. Each provider declares a wire dialect
(`openai` or `anthropic`), a base URL (server root, **without** a `/v1` version path — the inbound path is
appended verbatim), an upstream key, and optional `From → To` model mappings with per-mapping caching.

For the OpenAI **default passthrough** provider, choose the base URL that matches the caller's authentication:

- **OpenAI Platform / API-key auth:** use `"BaseUrl": "https://api.openai.com"` and provide an API key through
  config, user-secrets, environment variables, or caller passthrough.
- **Codex CLI with ChatGPT/subscription auth through Smooth:** use
  `"BaseUrl": "https://chatgpt.com/backend-api/codex"`. Smooth appends `/responses`, so this becomes
  `https://chatgpt.com/backend-api/codex/responses`.

Use the matching Codex CLI provider config with ChatGPT/subscription auth. For the default local `dotnet run`
port:

```toml
# ~/.codex/config.toml
model_provider = "smooth-llm-proxy"

[model_providers.smooth-llm-proxy]
name = "Smooth LLM Imposter"
base_url = "http://127.0.0.1:5080/openai"
wire_api = "responses"
requires_openai_auth = true
```

```jsonc
// appsettings.json (shipped example)
{
  "Imposter": {
    "Providers": [
      {
        "Name": "openai-default",
        "Dialect": "openai",
        "BaseUrl": "https://chatgpt.com/backend-api/codex",
        "IsDefault": true
      },
      {
        "Name": "opencode-go",
        "Dialect": "openai",
        "BaseUrl": "https://opencode.ai/zen/go",
        "ApiKey": "",
        "OpenAiUpstreamApi": "chat_completions",
        "Models": [
          { "From": "gpt-5.4", "To": "kimi-k2.7", "Caching": true }
        ]
      },
      {
        "Name": "opencode-anthropic",
        "Dialect": "anthropic",
        "BaseUrl": "https://opencode.ai/zen/go",
        "ApiKey": "",
        "Models": [
          { "From": "claude-haiku-*", "To": "minimax-m3", "Caching": true }
        ]
      }
    ]
  }
}
```

Routing rules to keep in mind:

- **First match wins, in configuration order.** The resolver scans the request dialect's providers top-to-bottom
  and takes the first `Models[].From` that matches. Order providers and mappings most-specific first.
- **`From` matching** is exact or a single trailing-`*` wildcard (`claude-haiku-*`), case-insensitive.
- **No match → passthrough** via the dialect's `IsDefault` provider (model unchanged, no caching). **No match and
  no default → 404.** The shipped `appsettings.json` declares catch-all `IsDefault` providers for `anthropic`
  (`https://api.anthropic.com`) and `openai` (`https://api.openai.com`), so unmatched models pass through to the
  real provider. Remove them for type-only impostering (unmatched → 404).
- **Transparent proxy.** The request is relayed to the upstream unchanged — all caller headers (incl.
  `anthropic-beta`, vendor `x-*`) and the body pass through; the only mutations are caching injection on a
  matched imposter route and the auth header. **Auth** is route-dependent: an imposter route sends the
  provider's configured key; a **key-less passthrough** (default provider, no `ApiKey`, no stored credential)
  forwards the caller's own `Authorization` / `x-api-key` — so the catch-all defaults need no key in the
  router. The caller's `anthropic-version` is forwarded as-is; `2023-06-01` (or a configured `AnthropicVersion`)
  is supplied only when the caller omits it.

### Supplying keys via environment (preferred)

Never commit real keys. Environment variables override `appsettings.json` (env wins), using the standard
double-underscore path syntax for the array index:

```bash
export Imposter__Providers__0__ApiKey="sk-your-opencode-key"
export Imposter__Providers__1__ApiKey="sk-your-anthropic-route-key"
dotnet run --project src/SmoothLlmImposter.Host
```

### Local debugging — .NET user secrets

For day-to-day debugging you can keep keys out of your shell entirely using **.NET user secrets** (the Host ships a
`UserSecretsId`, loaded in `Development` only). The full debugger + dev-secrets guide is in
[`setups/local-debug.run-smooth-llm-imposter.md`](setups/local-debug.run-smooth-llm-imposter.md):

```bash
cd src/SmoothLlmImposter.Host
dotnet user-secrets set "Imposter:Providers:0:ApiKey" "sk-your-opencode-key"   # note the ':' path separator
```

Precedence is `appsettings.json < user secrets (Dev) < environment variables` — so an exported
`Imposter__Providers__0__ApiKey` **overrides** the stored secret for a one-off run.

## Point your client at the router

Send standard OpenAI- or Anthropic-dialect requests to the router instead of the real provider. With the shipped
config, OpenAI model `gpt-5.4` is rewritten to `kimi-k2.7` and forwarded to opencode.

**Point the client at a dialect prefix.** The router selects the dialect from a `/openai` or `/anthropic` path
prefix and forwards everything after it to the matching upstream verbatim — so the client's own paths
(`/v1/chat/completions`, `/v1/responses`, `/v1/models`, `/v1/messages`, `/v1/messages/count_tokens`, …) all
proxy without per-endpoint configuration. This is what disambiguates shared paths like `/v1/models`, which is
identical for both dialects.

Set each client's base URL to **exactly what you'd use for the real provider, with `/openai` or `/anthropic`
inserted after the host**. That matters because the two SDKs differ: the OpenAI SDK appends bare paths
(`/responses`, `/models`) and expects `/v1` in the base, while the Anthropic SDK appends `/v1/messages` itself.
So the OpenAI base ends in `/openai/v1` and the Anthropic base ends in `/anthropic` (no `/v1`). The examples
here use `http://localhost:5080`; the Compose setup uses `http://localhost:5066`.

For **Codex CLI with ChatGPT/subscription authentication**, use the `model_provider` block shown in the routing
configuration section above instead of `openai_base_url`. That routes every local Codex model request for the
selected config/profile through Smooth, regardless of whether the active model is `gpt-5.4`, `gpt-5.5`, or a
model selected later with `/model`; Codex login, model-catalog refresh, web search, MCP servers, connectors, and
cloud tasks are separate network paths.

For generic OpenAI-compatible SDK/API-key clients, keep `/v1` in the client base URL because those clients append
bare paths like `/responses`, `/chat/completions`, and `/models`:

```toml
openai_base_url = "http://localhost:5080/openai/v1"
```

```bash
# Anthropic SDK appends /v1/messages itself, so NO /v1 here
export ANTHROPIC_BASE_URL="http://localhost:5080/anthropic"
```

```bash
# OpenAI dialect — prefix /openai, client appends /v1/chat/completions (also /v1/responses, GET /v1/models)
curl http://localhost:5080/openai/v1/chat/completions \
  -H "content-type: application/json" \
  -d '{ "model": "gpt5.4", "messages": [ { "role": "user", "content": "Say hello in one sentence." } ] }'

# Anthropic dialect — prefix /anthropic, client appends /v1/messages
curl http://localhost:5080/anthropic/v1/messages \
  -H "content-type: application/json" \
  -H "anthropic-version: 2023-06-01" \
  -d '{ "model": "claude-haiku-4-5", "max_tokens": 128, "messages": [ { "role": "user", "content": "Hello" } ] }'
```

The unprefixed `POST /v1/chat/completions`, `/v1/responses`, and `/v1/messages` routes still work for
back-compat, but unprefixed `/v1/models` is dialect-ambiguous and is not served — use the prefix.

SSE streams are forwarded unbuffered, so `"stream": true` works end-to-end.

## Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/health` | Liveness — returns `{"status":"ok"}` |
| _any_ | `/openai/{**path}` | OpenAI dialect — `{path}` forwarded verbatim (e.g. `/openai/v1/chat/completions`, `/openai/v1/models`) |
| _any_ | `/anthropic/{**path}` | Anthropic dialect — `{path}` forwarded verbatim (e.g. `/anthropic/v1/messages`, `/anthropic/v1/messages/count_tokens`) |
| `POST` | `/v1/chat/completions` | OpenAI dialect (legacy, unprefixed) |
| `POST` | `/v1/responses` | OpenAI dialect (legacy, unprefixed) |
| `POST` | `/v1/messages` | Anthropic dialect (legacy, unprefixed) |
| `POST`/`GET`/`PUT`/`DELETE` | `/admin/credentials[...]` | Stored passthrough-credential admin (auth required) |

Under a dialect prefix, a request with a JSON body containing a `model` is routed by that model (imposter
match or default passthrough); a body-less request (e.g. `GET /openai/v1/models`) has no model to match and
passes through to the dialect's default provider unchanged.

Errors are dialect-shaped: OpenAI `{error:{message,type}}`, Anthropic `{type:"error",error:{...}}`. Routing
failures map to 400/404; upstream transport failures map to 502.

## Credential admin API (optional)

The `/admin/credentials` group manages encrypted **passthrough** credentials (HLD 002) and requires PostgreSQL.
It is guarded by the `X-Admin-Api-Key` header, matched in constant time against:

- `Admin:ApiKey` — full admin (`CredentialAdmin` role; required for all mutations)
- `Admin:OperatorApiKey` — operator (authenticated, non-admin)

```bash
export Admin__ApiKey="choose-a-strong-admin-key"
export ConnectionStrings__ImposterDb="Host=localhost;Port=5432;Database=smoothllmimposter;Username=postgres;Password=postgres"

curl http://localhost:5080/admin/credentials -H "X-Admin-Api-Key: $Admin__ApiKey"
```

The default connection string (when `ConnectionStrings:ImposterDb` is unset) is
`Host=localhost;Port=5432;Database=smoothllmimposter;Username=postgres;Password=postgres`. Stored secrets are
encrypted at rest via ASP.NET Core Data Protection.
