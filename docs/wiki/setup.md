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
  `anthropic-beta`, vendor `x-*`) and the body pass through; sanctioned mutations include caching/auth and
  per-provider normalization/session features. The global decision-tool filter is an explicit default-off
  exception, including passthrough (see below). **Auth** is route-dependent: an imposter route sends the
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
structured (`_BASE_URL`, `_AUTH_SCHEME`, `_AUTH_HEADER`, `_DIALECT`, `_IS_DEFAULT`, `_ENABLED`,
`_OPENAI_UPSTREAM_API`, `_REQUEST_NORMALIZATION`, `_SESSION_FORWARDING`, `_STRIP_ENCRYPTED_CONTENT`,
`_TIMEOUT_SECONDS`, `_OPTIMIZE_MESSAGES`, `_ANTHROPIC_VERSION`). Matching is case-insensitive.

`_STRIP_ENCRYPTED_CONTENT=true` drops OpenAI ZDR `encrypted_content` reasoning items from the forwarded
`input` array, for an upstream that serves `/responses` but cannot decrypt them (LM Studio answers
HTTP 400 `Encrypted content is not supported.`). It is **off by default and not set in any shipped
`appsettings*.json`** — enable it per deployment by injecting the env var for the provider that needs it,
e.g. `LMSTUDIO_STRIP_ENCRYPTED_CONTENT=true`. It is independent of `_OPENAI_UPSTREAM_API`, so the
provider stays on `/responses`. Full design: [HLD 011](../hlds/011-zdr-encrypted-content-sanitation/README.md).

`_TIMEOUT_SECONDS=<seconds>` sets that provider's **base upstream header timeout**, which the forwarder
scales across its three attempts (base, 2×base, 3×base). Unset it defaults to `300`, i.e. the shipped
300/600/900 s ladder. Raise it for a slow upstream — a local LM Studio loading a large model can need
several minutes before it answers headers — e.g. `LMSTUDIO_TIMEOUT_SECONDS=900` (→ 900/1800/2700 s).
Accepted range is 1–3600; anything else fails startup with a message naming the provider. It bounds only
the wait for **response headers**: once the upstream starts answering, an SSE stream runs as long as the
caller stays connected. It is set in no shipped `appsettings*.json`. Full design:
[HLD 012](../hlds/012-per-provider-upstream-timeout/README.md).

`_OPTIMIZE_MESSAGES=false` opts one provider's routes out of the opt-in message optimizer
(`LLM_MESSAGES_ENABLED`, [HLD 017](../hlds/017-message-optimizer/README.md)). Leave it unset, or `true`, to
keep optimization on. Use it for request-billed upstreams such as a GitHub Copilot route, where smaller
messages save nothing and the gate and optimizer calls only add latency. The prefix is the provider key, so
the unprefixed pair uses `GITHUB_ANTHROPIC_OPTIMIZE_MESSAGES=false` and a prefixed one such as
`company1-github-anthropic` uses `COMPANY1_GITHUB_ANTHROPIC_OPTIMIZE_MESSAGES=false`. The tool filter is unaffected. Nothing sets it automatically,
and it is in no shipped `appsettings*.json`.

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
or the Anthropic dialect. The value is case-insensitive and accepts `opencode-go`, `opencode_go`, or
`opencodego` (all equivalent); `opencode-go` is the canonical spelling. Omit or set `none` to keep
byte-transparent behaviour unless the independent global decision-tool filter is enabled.

```bash
export OPENCODE_GO_SESSION_FORWARDING="opencode-go"
# equivalent structured form (per-provider, since the conventional prefix is what opts both siblings in):
export Imposter__Providers__opencode-go-openai__SessionForwarding="opencode-go"
export Imposter__Providers__opencode-go-anthropic__SessionForwarding="opencode-go"
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

### Optional decision-model tool pre-filter

For cache behavior when an agent adds or removes tools, see
[Prompt caching with dynamic agent tools](prompt-caching.md). Stable catalogs can improve prefix reuse;
neither the decision memo nor a session cache key guarantees an upstream cache hit.
Optional `Imposter__ToolOrdering__Enabled=true` remembers first-seen order per explicit session,
appends new tools and leaves removed tools removed; see the guide's
[ordering policy and limits](prompt-caching.md#optional-session-local-stable-ordering).

`DECISIONS_TOOLS_ENABLED=true` opts **all** supported imposter and passthrough routes into tool pruning
before forwarding. It is **off by default**; no shipped `appsettings*.json` or default container command
enables it. The [Conductor workspace installer](setups/conductor.build-smooth-llm-imposter.md#optional-decision-tool-filter)
forwards the variables below, and the [shared `DECISIONS_*` layer](#shared-decision-settings), without enabling the
feature. Configuration is read at startup; restart or recreate the container after changing it.

| Environment variable | `Imposter:DecisionsTools` field | Default | Enabled-only validation |
|---|---|---|---|
| `DECISIONS_TOOLS_ENABLED` | `Enabled` | `false` | — |
| `DECISIONS_TOOLS_BASE_URL` | `BaseUrl` | `http://host.docker.internal:1234` | Absolute HTTP(S) URL |
| `DECISIONS_TOOLS_PATH` | `Path` | `/v1/systemone` | Starts with `/` |
| `DECISIONS_TOOLS_MODEL` | `Model` | `openjev` | Non-empty |
| `DECISIONS_TOOLS_MIN_PROBABILITY` | `MinProbability` | `0.50` | 0–1 inclusive |
| `DECISIONS_TOOLS_MIN_KEEP` | `MinKeepTools` | `20` | 0–1000 (`0` disables the floor) |
| `DECISIONS_TOOLS_ALWAYS_KEEP` | `AlwaysKeep` | `Bash,Read,Edit,Write,Skill` | — (comma-separated tool names) |
| `DECISIONS_TOOLS_STICKY_PER_SESSION` | `StickyPerSession` | `false` | — |
| `DECISIONS_TOOLS_MAX_ATTEMPTS` | `MaxAttempts` | `3` | 1–10 inclusive |
| `DECISIONS_TOOLS_TIMEOUT` | `TimeoutSeconds` | `60` | 1–3600 seconds |
| `DECISIONS_TOOLS_CIRCUIT_BREAK_SECONDS` | `CircuitBreakSeconds` | `180` | 1–3600 seconds |

Every setting except `DECISIONS_TOOLS_ENABLED` falls back to the matching shared `DECISIONS_*` value when the tool
section leaves it unset, and only then to the default shown; see [Shared decision settings](#shared-decision-settings).

The timeout env name has **no `_SECONDS` suffix**. Decimal overrides use invariant notation (`0.50`, not
`0,50`). Conventional variables win over structured configuration; blank values are absent and invalid
boolean/numeric text warns without replacing the bound value. Disabled settings are not validated.

The decision service receives up to 2,000 characters of prompt state plus tool labels/descriptions; a remote
BaseUrl sends that data to the remote service, including for passthrough requests. Caller credentials and
headers are not sent. Classification is serialized in chunks of up to 20 tools and can add roughly
0.5 seconds per tool on the first request of a user turn; later agent steps with the same request and catalog
are answered from an in-memory cache and forward the same tools. Catalogs using deferred tool loading
(`defer_loading` or a `tool_search` tool) are not filtered, and every other top-level value besides `tools`/`tool_choice` is forwarded byte-for-byte. History-referenced and
unknown tools are kept, and a pass that would leave no tool keeps the whole catalog. Tools in
`DECISIONS_TOOLS_ALWAYS_KEEP` are never asked about or removed (the defaults are Claude Code's core tool names;
set your client's own names for other agents; `ToolSearch` and `Agent` are always kept in addition), and at least `DECISIONS_TOOLS_MIN_KEEP` tools are always
forwarded — the highest-scoring ones fill the floor — so a vague request cannot shrink the catalog to a handful.
Catalogs of that size or smaller are not classified at all. With `DECISIONS_TOOLS_STICKY_PER_SESSION=true`, a
tool the filter forwarded stays forwarded for the rest of that chat while the client still sends it, so the
upstream prompt cache is not invalidated by the filter between turns; it needs an explicit per-chat session id
(see the [caching guide](prompt-caching.md)) and trades growing tool tokens for a stable prefix. Each classified pass logs its model, endpoint, a session digest (`session none` without a captured session id)
and the removed tool names at Information level. Service failure keeps unresolved tools and continues inference;
the warning names the HTTP status, model and endpoint. A `400` on
every request usually means the model is not a decision model — LM Studio must list it with `"type": "decision"`
in `/api/v1/models` (for example `kev-4b` or `openjev`); a model it lists as `llm` is rejected as
`invalidModelIdentifier`.

For bare-metal `dotnet run` or a native executable, set `DECISIONS_TOOLS_BASE_URL=http://127.0.0.1:1234` (or the
shared `DECISIONS_BASE_URL`).
In Docker Desktop the default host name resolves natively; Linux Docker/Podman requires
`--add-host=host.docker.internal:host-gateway`. Container `127.0.0.1` points to the container, not the host.
See [Docker opt-in](setups/docker.run-smooth-llm-imposter.md#optional-decision-tool-filter) and
[HLD 014](../hlds/014-decisions-tools-filter/README.md).

### Shared decision settings

The tool filter and the message optimizer's gate ([HLD 017](../hlds/017-message-optimizer/README.md)) are two
sections of one decision model. Set the model once with the shared `DECISIONS_*` variables
(`Imposter:Decisions` structured section) and override a section only where it differs. Every section setting
resolves in this order ([LADR-06](../hlds/017-message-optimizer/ladrs/LADR-06-layered-settings.md)):

1. the section's own value — `DECISIONS_TOOLS_*` / `DECISIONS_MESSAGES_*`, or `Imposter:DecisionsTools` /
   `Imposter:DecisionsMessages`;
2. the shared `DECISIONS_*` value, or `Imposter:Decisions`;
3. the built-in default.

Within one layer the conventional variable wins over the structured key. Blank values count as unset at every
layer, and an invalid conventional value warns and falls through to the next layer. A section never takes a value
from the other section, and existing `DECISIONS_TOOLS_*`-only deployments resolve exactly as before.

| Shared variable | `Imposter:Decisions` field | Fills |
|---|---|---|
| `DECISIONS_BASE_URL` | `BaseUrl` | tool and message sections |
| `DECISIONS_PATH` | `Path` | tool and message sections |
| `DECISIONS_MODEL` | `Model` | tool and message sections |
| `DECISIONS_MIN_PROBABILITY` | `MinProbability` | tool and message sections |
| `DECISIONS_MAX_ATTEMPTS` | `MaxAttempts` | tool and message sections |
| `DECISIONS_TIMEOUT` | `TimeoutSeconds` | tool and message sections |
| `DECISIONS_CIRCUIT_BREAK_SECONDS` | `CircuitBreakSeconds` | tool and message sections |
| `DECISIONS_MIN_KEEP` | `MinKeepTools` | tool section only |
| `DECISIONS_ALWAYS_KEEP` | `AlwaysKeep` | tool section only |
| `DECISIONS_STICKY_PER_SESSION` | `StickyPerSession` | tool section only |

There is **no `DECISIONS_ENABLED`**: one variable must never switch on two features. Each section keeps its own
switch. Shared values are validated only through a section that is on.

The message gate's section (`Imposter:DecisionsMessages`):

| Environment variable | `Imposter:DecisionsMessages` field | Default | Validation while the gate is on |
|---|---|---|---|
| `DECISIONS_MESSAGES_ENABLED` | `Enabled` | value of `LLM_MESSAGES_ENABLED` | — |
| `DECISIONS_MESSAGES_BASE_URL` | `BaseUrl` | `http://host.docker.internal:1234` | Absolute HTTP(S) URL |
| `DECISIONS_MESSAGES_PATH` | `Path` | `/v1/systemone` | Starts with `/` |
| `DECISIONS_MESSAGES_MODEL` | `Model` | `openjev` | Non-empty |
| `DECISIONS_MESSAGES_MIN_PROBABILITY` | `MinProbability` | `0.50` | 0–1 inclusive |
| `DECISIONS_MESSAGES_MAX_ATTEMPTS` | `MaxAttempts` | `3` | 1–10 inclusive |
| `DECISIONS_MESSAGES_TIMEOUT` | `TimeoutSeconds` | `60` | 1–3600 seconds |
| `DECISIONS_MESSAGES_CIRCUIT_BREAK_SECONDS` | `CircuitBreakSeconds` | `180` | 1–3600 seconds |

The gate is on only while `LLM_MESSAGES_ENABLED=true` and `DECISIONS_MESSAGES_ENABLED` is unset or `true`.
The gate calls its own section's endpoint, path, model, attempts and timeout; the tool filter calls the tool
section's. Each decision call waits for the admission gate of its resolved endpoint and model: when both sections
resolve to the same pair they take turns (one call at a time), and on different endpoints or models they run in
parallel.

**Message optimizer time budget.** `LLM_MESSAGES_BUDGET_SECONDS` (`Imposter:LlmMessages:BudgetSeconds`, optional,
default `30`, validated 1–600 while `LLM_MESSAGES_ENABLED=true`) bounds the time one request spends on gate and
optimizer calls, admission waits included, before it is forwarded. When it runs out the call in flight is cancelled,
rewrites finished earlier in the request still apply, and every remaining message is forwarded and remembered as
written, so later turns replay it unchanged; no circuit opens. One Information line per such request reports the
counts (`... budget of 30 s exhausted: 2 of 3 candidate segments forwarded as the original`). Raise it when a slow
local optimizer leaves too much of a new conversation unoptimized; lower it to cap the first request's latency after
a restart or enablement ([HLD 017 Open item 7](../hlds/017-message-optimizer/README.md#open-items)).

**Upgrade note:** the message section never copies `DECISIONS_TOOLS_*`. Before per-section profiles the gate used
the tool section's transport, so a deployment that points only `DECISIONS_TOOLS_*` at a non-default classifier now
sends the gate to the built-in default (`http://host.docker.internal:1234/v1/systemone`, model `openjev`). Startup
logs one Warning naming both endpoints when that happens. Set the shared `DECISIONS_*` value (both sections) or the
`DECISIONS_MESSAGES_*` one to keep the gate on your classifier.

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

Keep `wire_api = "responses"` for Codex. The Codex CLI dropped `wire_api = "chat"` support
([discussion](https://github.com/openai/codex/discussions/7782)) — it only speaks the Responses API.
When a matched imposter provider uses `OpenAiUpstreamApi = "chat_completions"` (for OpenAI-compatible
upstreams that lack `/responses`), Smooth downgrades the outbound `/responses` request to Chat Completions
and translates the response back. This downgrade rejects Responses-native input types (e.g.
`additional_tools`). The `gpt-5.6-luna → grok-4.5` route is configured under `opencode-go-openai-responses`
(`OpenAiUpstreamApi=responses`) for future testing, but currently returns 422 because no provider serves
`/v1/responses` for these models yet. To re-enable the route when a provider adds `/v1/responses` support,
follow the "Re-enable by confirming provider support" steps in
[`AGENTS.md` Migration Plans](../../src/SmoothLlmImposter.Application/Features/Routing/AGENTS.md#migration-plans);
the route will work without further configuration changes.

### Bridging Claude Code to a Chat-Completions-only upstream (HLD 016)

An Anthropic-dialect provider can be opted into the Messages→Chat bridge so Claude Code can use an
OpenAI-Chat-Completions-only upstream. Set `AnthropicUpstreamApi = "chat_completions"` (default `"messages"`)
on the provider; conventional env: `<PROVIDER>_ANTHROPIC_UPSTREAM_API=chat_completions`. The provider keeps
its `anthropic` dialect and its model mappings; only a **matched imposter route** is bridged — passthrough and
native `messages` routes stay byte-identical. The bridge requires an effective **`AuthScheme` of `Bearer`**
(the Anthropic dialect default `ApiKey` fails startup and admin upsert).

The bridge is off in every shipped `appsettings*.json`; enable it per deployment. Tuning options (provider
keys, or conventional `<PROVIDER>_<FIELD>` env vars) and their defaults:

| Option | Values | Default |
|---|---|---|
| `CompatProfile` | `none`, `deepseek`, `minimax`, `mimo`, `zhipu` | `none` |
| `SendReasoningEffort` | boolean | `false` |
| `UserIdMode` | `off`, `hash`, `passthrough` | `off` |
| `MaxTokensField` | `max_tokens`, `max_completion_tokens` | `max_tokens` |
| `MaxOutputTokens` | integer ≥ 16, or unset | unset |
| `ForwardCacheControl` | boolean | `false` |
| `SendResponseFormat` | boolean | `false` |
| `DocumentMode` | `placeholder`, `image` | `placeholder` |
| `ThinkMarkupExtraction` | boolean | `false` |
| `MidturnSystem` | `system`, `merge_into_user` | `system` |

`SendReasoningEffort` and `UserIdMode` are off by design: Claude Code has thinking on by default, so sending
reasoning controls by default would 400 on strict upstreams, and `metadata.user_id` embeds device/account
identifiers. Enable them per provider only after verifying the upstream. `DocumentMode: image` is only for a
vision-capable upstream that accepts PDFs/images.

Limitations are visible rather than silent: hosted/server tool definitions are rejected with HTTP 400; an
unsupported content block in history becomes a text placeholder (`[omitted: unsupported content block '<type>']`);
thinking blocks carry an empty signature (display-only); token counts are estimates and `POST /v1/messages/count_tokens`
is answered locally without calling the upstream; long tool inputs reach the client when the call completes.
Claude Code points at the Anthropic prefix (`ANTHROPIC_BASE_URL=http://127.0.0.1:5080/anthropic`), and the
bridge performs one upstream attempt with no bridge-side retry.

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
| `POST` | `/admin/github-copilot/logins` · `.../{loginId}/complete` | Opt-in GitHub Copilot device login (auth required; flag on only) |
| `GET` | `/admin/github-copilot/connections` · `.../{connectionId}` · `.../{connectionId}/models` | Copilot connection status and model discovery (auth required; flag on only) |
| `GET` | `/scalar/v1` · `/openapi/v1.json` | Scalar UI and OpenAPI contract for the Copilot workflow (flag on only) |

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

## GitHub Copilot (opt-in)

Setting `ENABLE_GITHUB_COPILOT=true`, pointing `GithubCopilot__DatabasePath` at a writable directory and mounting
a dedicated volume adds GitHub Copilot as an upstream. One device login connects a protocol pair —
`github-anthropic` + `github-openai`, or `<prefix>-github-anthropic` / `<prefix>-github-openai` — and routing
stays same-dialect: Anthropic callers reach `/v1/messages`, OpenAI/GPT callers reach `/responses`. Copilot Chat
and cross-dialect translation are not supported.

Register **your own** GitHub OAuth application with device flow enabled (client ID only, no client secret) and
follow the ordered workflow:

```
01 POST /admin/github-copilot/logins               → verification URI + user code
02 POST .../logins/{loginId}/complete              → poll; 202 pending / 200 connected / 503 saved-not-published
03 GET  .../connections/{connectionId}/models      → account models by Messages/Responses surface
04 PUT  /admin/providers/<key>                     → From/To mapping (secret-free, existing surface)
05 GET  .../connections/{connectionId}             → publication + routing readiness
```

Every response carries a `message` field naming the next step, so curl is self-service. Credentials live in
SQLite with an isolated Data Protection key ring inside the dedicated volume; removing that exact volume is the
only way to delete them in v1, and GitHub-side revocation remains manual.

Full guide — Docker run and compose overlay, every curl, status codes, removal semantics and the threat
boundary: **[`setups/github-copilot.configure-smooth-llm-imposter.md`](setups/github-copilot.configure-smooth-llm-imposter.md)**.
Design: [HLD 013](../hlds/013-github-copilot/README.md).
