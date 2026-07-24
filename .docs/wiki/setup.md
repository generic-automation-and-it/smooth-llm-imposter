# Setup

> **What this is.** SmoothLlmImposter is a stateless, key-less LLM request router. It exposes OpenAI- and
> Anthropic-dialect endpoints, reads the inbound `model`, and either rewrites it to a configured upstream
> ("imposter") — optionally injecting prompt caching — or passes it through. Keys come from config/env only;
> nothing about a request is persisted. There is **no `claude login` inside the Host and no token capture** — you
> run the Host with `dotnet`, pull the published GHCR image, or build a local image from the repo
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
| **Message debug / logging** | Flip the router to `Debug` to dump the full inbound request (headers + body, auth masked) — no rebuild. | [`setups/logging.debug-smooth-llm-imposter.md`](setups/logging.debug-smooth-llm-imposter.md) |
| **Credential admin / authorization override** | _Optional._ Provider-keyed passthrough credentials (`/admin/credentials`, HLD 008) + provider-addressable force-Bearer override (HLD 003 amended by HLD 008). No DB required (in-memory default; PostgreSQL opt-in). | [`setups/credentials.admin-smooth-llm-imposter.md`](setups/credentials.admin-smooth-llm-imposter.md) |

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
appended verbatim), an optional upstream `Secret` plus `AuthScheme`, and optional `From → To` model mappings with
per-mapping caching.

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

The shipped provider set lives in **[`src/SmoothLlmImposter.Host/appsettings.json`](../../src/SmoothLlmImposter.Host/appsettings.json)**
— read current provider keys and URLs there rather than copying them here. Setup docs describe the stable shape
and conventions; duplicating the full shipped table here drifts. Each entry is a provider key →
`{ Dialect, BaseUrl, Secret, AuthScheme, optional IsDefault / OpenAiUpstreamApi, Models[] }`.

Routing rules to keep in mind:

- **First match wins, in configuration order.** The resolver scans the request dialect's providers top-to-bottom
  and takes the first `Models[].From` that matches. Order providers and mappings most-specific first.
- **`From` matching** is exact or a single trailing-`*` wildcard (`claude-haiku-*`), case-insensitive.
- **No match → passthrough** via the dialect's `IsDefault` provider (model unchanged, no caching). **No match and
  no default → 404.** When the active config declares catch-all `IsDefault` providers, unmatched models pass
  through to the configured upstream. Remove the defaults for type-only impostering (unmatched → 404).
- **Transparent proxy.** The request is relayed to the upstream unchanged — all caller headers (incl.
  `anthropic-beta`, vendor `x-*`) and the body pass through; the only mutations are caching injection on a
  matched imposter route and the auth header. **Auth** is route-dependent: an imposter route sends the
  provider's configured `Secret` using its `AuthScheme`; a **key-less passthrough** (default provider, no
  `Secret`, no stored credential)
  forwards the caller's own `Authorization` / `x-api-key` — so the catch-all defaults need no key in the
  router. The caller's `anthropic-version` is forwarded as-is; `2023-06-01` (or a configured `AnthropicVersion`)
  is supplied only when the caller omits it.

### Supplying keys via environment (preferred)

Never commit real keys. Environment variables override `appsettings.json` (env wins). Providers are keyed by
**name** (never by array index), so an override is addressed by the provider's name and survives any
reordering. There are two equivalent paths:

**Conventional `<NAME>_<FIELD>` surface (the friendly path).** Each provider exposes an env prefix derived
from its key — uppercase, every run of non-alphanumeric characters collapsed to one `_`. For dialect-suffixed
siblings, `_API_KEY` can use the shared base prefix (`opencode-go-openai` / `opencode-go-anthropic` →
`OPENCODE_GO_API_KEY`). The secret is reachable via three suffixes that all fill the same `Secret` —
`_API_KEY` (api-key-typed) plus the Bearer-typed aliases `_AUTH_TOKEN` (mirroring the Claude Code / Anthropic
SDK `ANTHROPIC_AUTH_TOKEN`) and `_AUTHORIZATION_BEARER`. **Which suffix wins follows the provider's effective
auth scheme:** a `Bearer` provider prefers `_AUTH_TOKEN` → `_AUTHORIZATION_BEARER` → `_API_KEY`, an `ApiKey`
provider prefers `_API_KEY` → `_AUTH_TOKEN` → `_AUTHORIZATION_BEARER` (the off-scheme suffixes stay as
fallbacks, so a single populated var still authenticates). This keeps a personal `ANTHROPIC_API_KEY` from being
sent as a Bearer token, and vice versa. Other scalar overrides remain provider-specific or
structured (`_BASE_URL`, `_AUTH_SCHEME`, `_AUTH_HEADER`, `_DIALECT`, `_IS_DEFAULT`, `_OPENAI_UPSTREAM_API`,
`_REQUEST_NORMALIZATION`, `_SESSION_FORWARDING`, `_ANTHROPIC_VERSION`). Matching is case-insensitive.

`_AUTH_SCHEME` picks the value format **and** the default header (`Bearer` → `Authorization: Bearer <token>`,
`ApiKey` → `x-api-key: <token>`). `_AUTH_HEADER` overrides only the **header name** — the value format still
follows the scheme. A gateway that expects the credential in a non-standard header sets it: e.g. a provider
with `AuthScheme: ApiKey` + `AuthHeader: api-key` (`<PREFIX>_AUTH_HEADER=api-key`) sends `api-key: <token>`
instead of `x-api-key: <token>`.

`_SESSION_FORWARDING` opts a provider into session-identity stamping on **matched imposter routes** (HLD 009).
Set it to `opencode-go` for the shipped `opencode-go-*` providers so Codex/Claude traffic is grouped in
opencode-go diag (`session_id` body field on OpenAI + `x-opencode-session` header). Like `_API_KEY` above, the
conventional prefix is shared across dialect-suffixed siblings — `OPENCODE_GO_SESSION_FORWARDING` opts in **both**
`opencode-go-openai` and `opencode-go-anthropic`, so the stamp applies whether the traffic arrives on the OpenAI
or the Anthropic dialect. Omit or set `none` to keep byte-transparent behaviour.

```bash
export OPENCODE_GO_SESSION_FORWARDING="opencode-go"
# equivalent structured form:
export Imposter__Providers__opencode-go-openai__SessionForwarding="opencode-go"
```

```bash
export OPENCODE_GO_API_KEY="sk-your-opencode-key"            # opencode-go-openai + opencode-go-anthropic secret
export OPENROUTER_API_KEY="sk-your-openrouter-key"           # openrouter-openai + openrouter-anthropic secret
dotnet run --project src/SmoothLlmImposter.Host
```

**Structured `Imposter__Providers__<name>__<Field>` surface (the equivalent .NET path).** Same effect, using
the double-underscore section path keyed by provider name:

```bash
export Imposter__Providers__opencode-go-openai__Secret="sk-your-opencode-key"
export Imposter__Providers__opencode-go-openai__AuthScheme="ApiKey"
```

When the same field is set both ways, the **conventional** value wins.

### Local debugging — .NET user secrets

For day-to-day debugging you can keep keys out of your shell entirely using **.NET user secrets** (the Host ships a
`UserSecretsId`, loaded in `Development` only). The full debugger + dev-secrets guide is in
[`setups/local-debug.run-smooth-llm-imposter.md`](setups/local-debug.run-smooth-llm-imposter.md):

```bash
cd src/SmoothLlmImposter.Host
dotnet user-secrets set "Imposter:Providers:opencode-go-openai:Secret" "sk-your-opencode-key"   # note the ':' path separator
dotnet user-secrets set "Imposter:Providers:opencode-go-openai:AuthScheme" "ApiKey"
```

Precedence is `appsettings.json < user secrets (Dev) < structured env < conventional env` — so an exported
`OPENCODE_GO_API_KEY` (or the structured `Imposter__Providers__opencode-go-openai__Secret`) **overrides** the stored
secret for a one-off run.

## Point your client at the router

Send standard OpenAI- or Anthropic-dialect requests to the router instead of the real provider. The shipped
config has no model rewrites committed; configure an imposter provider in `appsettings.json` (or via env) to
route specific inbound models to an alternate upstream.

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

Keep `wire_api = "responses"` for Codex even when a matched imposter provider uses
`OpenAiUpstreamApi = "chat_completions"` (for example `opencode-go-openai`). Smooth downgrades the outbound
`/responses` request to Chat Completions for that upstream and translates the Chat response stream back into
Responses events for Codex.

For generic OpenAI-compatible SDK/API-key clients, keep `/v1` in the client base URL because those clients append
bare paths like `/responses`, `/chat/completions`, and `/models`:

```toml
openai_base_url = "http://localhost:5080/openai/v1"
```

```bash
# Anthropic SDK appends /v1/messages itself, so NO /v1 here
export ANTHROPIC_BASE_URL="http://localhost:5080/anthropic"
```

For Claude subscription-based upstreams, create a token with the Claude CLI and supply it yourself as the
imposter provider `Secret` override:

```bash
claude setup-token

# Example: openrouter-anthropic is the shipped Anthropic-dialect imposter path.
export OPENROUTER_API_KEY="paste-the-openrouter-key-here"
```

Use `AuthScheme="Bearer"` when the upstream expects `Authorization: Bearer <token>`. Use `AuthScheme="ApiKey"`
when the upstream expects `x-api-key: <token>`.

### Personal-subscription providers (`anthropic-personal` / `openai-personal`)

The shipped config includes two **personal-subscription** provider scaffolds for the common "company
subscription for daily use, personal subscription for private use" split. They ship inert (no `Models[]`) and
with an empty `Secret`; add the `Models[]` mappings you want routed to your subscription at deploy time, then
supply your token via env. Both shipped personal providers are `Bearer`, so the conventional secret var follows
that scheme: a `Bearer` provider prefers the auth-typed `_AUTH_TOKEN` / `_AUTHORIZATION_BEARER`; `_API_KEY` is
an accepted fallback that fills the same `Secret` (the off-scheme suffix stays a fallback so a single populated
var still authenticates). All three suffixes fill the same `Secret`:

```bash
export ANTHROPIC_PERSONAL_AUTH_TOKEN="paste-your-anthropic-subscription-token"
export OPENAI_PERSONAL_AUTH_TOKEN="paste-your-openai-subscription-token"
# (_AUTHORIZATION_BEARER is equivalent; _API_KEY is a fallback.)
```

#### Minting the tokens

**Anthropic — `claude setup-token`.** Claude Code can mint a long-lived subscription token. If you
normally run Claude Code on a **company** license, you can grab a **personal** token without losing that
session for long:

```bash
claude login          # sign in with your PERSONAL Claude account, then exit the interactive session
claude setup-token    # prints a long-lived subscription token from that personal session — copy it
claude logout && claude login   # sign back in with your COMPANY account for day-to-day use
```

Then paste the copied value into `ANTHROPIC_PERSONAL_AUTH_TOKEN`.

**OpenAI / Codex — no `setup-token` equivalent.** Codex CLI has no one-shot portable-token command
([OpenAI Codex auth docs](https://developers.openai.com/codex/auth)). The supported ways to get a
ChatGPT-subscription credential for this router:

- `codex login` with your personal ChatGPT account (on a trusted machine) writes `~/.codex/auth.json`,
  which holds the access/refresh tokens. Extract the current access token and paste it — e.g.
  `jq -r '.tokens.access_token' ~/.codex/auth.json`. **Caveat:** that token is short-lived and Codex
  auto-refreshes it *inside* `auth.json`, so a statically pasted Bearer **will expire** — fine for a
  dev/short-lived run, not for a long-running deployment.
- **ChatGPT Business/Enterprise:** an admin can mint a dedicated, non-interactive **access token** in
  the ChatGPT admin console ([access tokens](https://developers.openai.com/codex/enterprise/access-tokens)) —
  the durable option.
- Or skip the subscription entirely and use a plain OpenAI **API key** as the provider `Secret`
  (per-token billing); this is OpenAI's recommended path for non-interactive use.

> **Check your plan's terms first.** A provider's terms may restrict using subscription OAuth tokens
> outside its official apps/CLI (Anthropic clarified this for Claude Free/Pro/Max in early 2026). Route a
> personal subscription token through this proxy only where your plan permits it; a provider-issued API
> key is the unambiguous path.

- `anthropic-personal` and `openai-personal` ship **inert** (no `Models`) — they are templates you activate by
  adding your own `Models[]` entries at deploy time. (See HLD 007 LADR-04 for the personal-subscription pattern.)

```bash
# OpenAI dialect — prefix /openai, client appends /v1/chat/completions (also /v1/responses, GET /v1/models)
curl http://localhost:5080/openai/v1/chat/completions \
  -H "content-type: application/json" \
  -d '{ "model": "gpt-5.4", "messages": [ { "role": "user", "content": "Say hello in one sentence." } ] }'

# Anthropic dialect — prefix /anthropic, client appends /v1/messages
curl http://localhost:5080/anthropic/v1/messages \
  -H "content-type: application/json" \
  -H "anthropic-version: 2023-06-01" \
  -d '{ "model": "claude-haiku-4-5", "max_tokens": 128, "messages": [ { "role": "user", "content": "Hello" } ] }'
```

The unprefixed `POST /v1/chat/completions`, `/v1/responses`, and `/v1/messages` routes still work for
back-compat, but unprefixed `/v1/models` is dialect-ambiguous and is not served — use the prefix.

SSE streams are forwarded unbuffered. On the `/responses`→Chat downgrade path, the stream is translated
incrementally back into Responses events; other streaming routes are relayed byte-for-byte.

### Strict upstream tool-name validation (Codex → opencode/Moonshot)

Some upstreams validate tool **function names** more strictly than OpenAI does. Moonshot/kimi (reached via
`opencode-go-openai`) requires names matching `^[a-zA-Z][a-zA-Z0-9_-]*$` — start with a letter; letters, numbers,
underscores, dashes only. A tool-using turn whose tools break that rule returns **HTTP 400** from the
upstream:

```
Error from provider (Moonshot AI): Invalid request: function name is invalid, must start with a
letter and can contain letters, numbers, underscores, and dashes
```

For `OpenAiUpstreamApi = "chat_completions"` imposter routes, Smooth enables `CodexToOpenAiSdk` request
normalization by default. It keeps upstream-valid `function` tools, flattens namespace wrappers, removes
unsupported tool types, drops invalid function names such as dotted names, and cleans any `tool_choice`
that pointed at a removed tool. It never renames tools or rewrites prior-turn tool history. Set
`RequestNormalization = "none"` on a provider only when you need the raw tool catalog forwarded unchanged.

Upstreams with lenient validation (OpenAI itself) do not need this normalization; `responses` upstreams keep it
off by default.

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

## Credential admin API & authorization override (optional)

The `/admin/credentials` API (provider-keyed **passthrough** credentials, HLD 008) and the provider-addressable
authorization override (HLD 003 amended by HLD 008) work without PostgreSQL. With `ConnectionStrings:ImposterDb`
unset the router uses **no database** and keeps credentials in memory until restart. Set the connection string
only when you want encrypted PostgreSQL persistence. Imposter routes — including the `*-personal` providers —
never use this store; they authenticate with their config/env `Secret`.

Full setup (enabling it, admin auth, every endpoint incl. the PUTs, Data Protection keys) lives in the
dedicated guide: **[`setups/credentials.admin-smooth-llm-imposter.md`](setups/credentials.admin-smooth-llm-imposter.md)**.
