# Conductor → SmoothLlmImposter routing setup

## TL;DR

Running this script in a fresh Conductor sandbox **builds SmoothLlmImposter from source and runs it on
`localhost:5080`**. It installs the .NET 10 SDK, publishes the Host, and starts it behind a single idempotent
helper (`imposter-up.sh`). Point any OpenAI- or Anthropic-dialect client's base URL at `http://localhost:5080`
and the router rewrites the inbound `model` to a configured upstream per the **`Imposter`** config section. With
the shipped `appsettings.json`:

| Inbound dialect / model | Rewritten to | Upstream provider |
|:------------------------|:-------------|:------------------|
| OpenAI `gpt-5.4`        | `kimi-k2.7-code`  | opencode-go-openai (`https://opencode.ai/zen/go`) |
| Anthropic `claude-opus-4-7*` | `claude-opus-4-8` | anthropic-personal (`https://api.anthropic.com`, personal Bearer token) |
| Anthropic `claude-opus-4-6*` | `z-ai/glm-5.2` | openrouter-anthropic (`https://openrouter.ai/api`) |
| Anthropic `claude-haiku-*` | `minimax-m3` | opencode-go-anthropic (`https://opencode.ai/zen/go`) |

The router is **stateless and key-less**: it does not capture or persist the caller's auth, and it does not run
as a container. Each provider's upstream key comes from the environment (`<NAME>_API_KEY` conventional, or the
structured `Imposter__Providers__<name>__Secret`, with a sibling `<NAME>_AUTH_SCHEME` /
`Imposter__Providers__<name>__AuthScheme` of `ApiKey`|`Bearer` selecting the header, defaulting by dialect); the
inbound `Authorization` / `x-api-key` is replaced by the provider's configured key. Unmatched models return 404
unless a provider sets `"IsDefault": true` (passthrough).

## Prerequisites / knobs

- **`<NAME>_API_KEY`** / **`<NAME>_AUTH_TOKEN`** (conventional) or **`Imposter__Providers__<name>__Secret`**
  (structured) — the upstream key for the named provider, where `<NAME>` is the uppercased provider key (or the
  shared base prefix for dialect-suffixed siblings, e.g. `opencode-go-openai` / `opencode-go-anthropic` →
  `OPENCODE_GO_API_KEY`, `openrouter-openai` / `openrouter-anthropic` → `OPENROUTER_API_KEY`).
  Identity is the name, so it is order-independent. `_API_KEY` and `_AUTH_TOKEN` fill the **same** provider
  `Secret`; which one wins **follows the effective auth scheme** — a `Bearer` provider prefers `_AUTH_TOKEN`, an
  `ApiKey` provider prefers `_API_KEY` (the off-scheme var stays a fallback). The sibling provider-specific
  **`<NAME>_AUTH_SCHEME`** / **`Imposter__Providers__<name>__AuthScheme`** (`ApiKey`|`Bearer`, case-insensitive)
  selects the auth header and defaults by dialect when omitted (openai → Bearer, anthropic → ApiKey).
  **`export` it in your shell before running** — do **not** paste a real key into the script block below, because
  this file is tracked in the repo and a committed key would leak. The Host starts without keys, but any routed
  request to that provider fails upstream. Keys are written only to `~/.config/smooth-llm-imposter/imposter.env`
  (mode 600, outside the repo) — never to a tracked file and never printed to the console.
- **`ASPNETCORE_URLS`** — optional. Bind address; the script defaults to `http://+:5080`.
- **`Imposter__Providers__<name>__BaseUrl` / `__To` / `__Caching`** — optional per-provider overrides of the
  shipped `appsettings.json` routing table, keyed by provider name. `BaseUrl` is the server root **without** a `/v1`
  path (the inbound path is
  appended verbatim; adding `/v1` double-prefixes).
- **`Admin__ApiKey` / `Admin__OperatorApiKey`** — only needed to use the `/admin/credentials` API. The admin key
  grants the `CredentialAdmin` role (all mutations); the operator key authenticates without it.
- **`ConnectionStrings__ImposterDb`** — only needed when credential-admin data should persist in PostgreSQL.
  Pure imposter routing needs no database. **When unset, the router uses no database** and stores
  credential-admin data in memory until restart; there is no runtime localhost fallback. See
  [`credentials.admin-smooth-llm-imposter.md`](credentials.admin-smooth-llm-imposter.md).

## Why the helper + env-file shape

- A single helper, `~/.config/smooth-llm-imposter/imposter-up.sh`, is the **one source of truth** for "ensure the
  published Host is built and running". It publishes once if the build is missing, then starts the process only if
  it isn't already listening on the port — so it is safe to run repeatedly and from the `~/.bashrc` hook.
- Config (provider keys + admin keys + connection string) lives in `~/.config/smooth-llm-imposter/imposter.env`
  (mode 600) and is loaded via `set -a; . imposter.env; set +a`, so the auto-start hook can re-run without secrets
  in shell config and without re-passing keys on the command line. Secrets are never echoed (the verify step
  checks presence only).

## Script

```bash
#!/usr/bin/env bash
set -euo pipefail

# ──────────────────────────────────────────────────────────────────────
#  Fresh-sandbox setup: install the .NET 10 SDK, publish the
#  SmoothLlmImposter Host, and run it on localhost:5080. A single helper
#  (~/.config/smooth-llm-imposter/imposter-up.sh) is the ONE source of
#  truth for "ensure the Host is built and running". It is safe to run
#  repeatedly and is called both here and from the ~/.bashrc hook.
# ──────────────────────────────────────────────────────────────────────

PORT="${PORT:-5080}"
REPO_DIR="${REPO_DIR:-$(git rev-parse --show-toplevel 2>/dev/null || pwd)}"
CONF_DIR="$HOME/.config/smooth-llm-imposter"
STATE_DIR="$HOME/.local/state/smooth-llm-imposter"
ENV_FILE="$CONF_DIR/imposter.env"
HELPER="$CONF_DIR/imposter-up.sh"
PUBLISH_DIR="$STATE_DIR/publish"
LOG_FILE="$STATE_DIR/imposter.log"

mkdir -p "$CONF_DIR" "$STATE_DIR"

# Prefer `export OPENCODE_GO_API_KEY=...` for opencode-go-* (etc.) in your shell before
# running. Do NOT commit real keys here — this file is tracked in the repo.
: "${ASPNETCORE_URLS:=http://+:$PORT}"

# ── Write the env file (mode 600) from whatever is exported. Empty values are
#    fine; the Host starts without keys but routed calls to that provider fail.
umask 077
{
  echo "ASPNETCORE_URLS=$ASPNETCORE_URLS"
  for var in $(compgen -e | grep -E '^(Imposter__|Admin__|ConnectionStrings__)|^[A-Z0-9_]+_(API_KEY|AUTH_TOKEN|AUTHORIZATION_BEARER|AUTH_SCHEME|BASE_URL|DIALECT|IS_DEFAULT|OPENAI_UPSTREAM_API|REQUEST_NORMALIZATION|ANTHROPIC_VERSION)$'); do
    printf '%s=%s\n' "$var" "${!var}"
  done
} > "$ENV_FILE"
chmod 600 "$ENV_FILE"

# ── Install the .NET 10 SDK if no suitable dotnet is on PATH.
if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks | grep -q '^10\.'; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$PATH"
fi

# ── The one-source-of-truth helper.
cat > "$HELPER" <<HELPER_EOF
#!/usr/bin/env bash
set -euo pipefail
export PATH="\$HOME/.dotnet:\$PATH"
REPO_DIR="$REPO_DIR"
PUBLISH_DIR="$PUBLISH_DIR"
ENV_FILE="$ENV_FILE"
LOG_FILE="$LOG_FILE"
PORT="$PORT"

# Publish once if the build is missing.
if [ ! -x "\$PUBLISH_DIR/SmoothLlmImposter.Host" ]; then
  dotnet publish "\$REPO_DIR/src/SmoothLlmImposter.Host" -c Release -o "\$PUBLISH_DIR"
fi

# Already listening? Nothing to do.
if curl -fsS "http://localhost:\$PORT/health" >/dev/null 2>&1; then
  echo "SmoothLlmImposter already up on :\$PORT"
  exit 0
fi

# Load config (secrets stay out of shell history / process args) and start detached.
set -a; . "\$ENV_FILE"; set +a
nohup "\$PUBLISH_DIR/SmoothLlmImposter.Host" >>"\$LOG_FILE" 2>&1 &
echo "SmoothLlmImposter starting on :\$PORT (logs: \$LOG_FILE)"
HELPER_EOF
chmod +x "$HELPER"

# ── Start it now.
"$HELPER"

# ── Point the Codex CLI at the router (ChatGPT/subscription auth). A fresh
#    cloud sandbox has no ~/.codex/config.toml, so recreate it here. The
#    base_url carries the /openai dialect prefix; Codex appends /responses.
mkdir -p "$HOME/.codex"
cat > "$HOME/.codex/config.toml" <<CODEX_EOF
model_provider = "smooth-llm-proxy"

[model_providers.smooth-llm-proxy]
name = "Smooth LLM Imposter"
base_url = "http://127.0.0.1:$PORT/openai"
wire_api = "responses"
requires_openai_auth = true
CODEX_EOF

# ── Auto-start on new shells (idempotent; the helper no-ops if already up).
HOOK_LINE=". \"$HELPER\" >/dev/null 2>&1 || true"
grep -qF "$HELPER" "$HOME/.bashrc" 2>/dev/null || echo "$HOOK_LINE" >> "$HOME/.bashrc"

# ── Wait for liveness.
for _ in $(seq 1 30); do
  curl -fsS "http://localhost:$PORT/health" >/dev/null 2>&1 && break
  sleep 1
done
curl -fsS "http://localhost:$PORT/health" && echo "  <- SmoothLlmImposter is up on :$PORT"
```

## Point Codex CLI at the router

The script above already writes `~/.codex/config.toml` with a `model_provider` override so the Codex CLI
(ChatGPT/subscription auth) drives the router out of the box — a fresh Conductor cloud sandbox starts with no
`~/.codex/config.toml` (new Linux home directory), so the script recreates it. The written file is:

```toml
# ~/.codex/config.toml
model_provider = "smooth-llm-proxy"

[model_providers.smooth-llm-proxy]
name = "Smooth LLM Imposter"
base_url = "http://127.0.0.1:5080/openai"
wire_api = "responses"
requires_openai_auth = true
```

The `base_url` is the router root **plus the `/openai` dialect prefix** — Codex's Responses-API client appends
`/responses`, and the router selects the OpenAI dialect from the prefix and forwards the rest of the path
verbatim. The script substitutes `$PORT` into the URL, so it stays aligned if you override the port.

The provider selection applies to every local Codex model request for the selected config/profile, including
models picked later with `/model`. It does not proxy Codex login, model-catalog refresh, web search, MCP servers,
connectors, or cloud tasks. Keep `wire_api = "responses"` even when a matched imposter provider's upstream speaks
`chat_completions` — Smooth converts and re-emits Responses events for Codex.

> If your only customization is MCP servers (not the provider override), commit a project-level
> `.codex/config.toml` to the repo root instead: cloud workspaces clone the repo on startup, so it's present
> without any setup-script change.

## Verify routing end-to-end

After setup, from the sandbox:

```bash
curl -fsS localhost:5080/health        # {"status":"ok"}
```

Send a routed OpenAI-dialect request — with the shipped config, `gpt-5.4` is rewritten to `kimi-k2.7-code` and
forwarded to opencode-go-openai (requires `OPENCODE_GO_API_KEY`, or the structured `Imposter__Providers__opencode-go-openai__Secret`,
to be set, or the upstream returns an auth error):

```bash
curl -fsS localhost:5080/v1/chat/completions \
  -H "content-type: application/json" \
  -d '{ "model": "gpt-5.4", "messages": [ { "role": "user", "content": "Say hello in one sentence." } ] }'
```

Watch the Host log for the model swap and upstream forward:

```bash
tail -f ~/.local/state/smooth-llm-imposter/imposter.log
```

A model that matches no mapping (and with no `IsDefault` provider) returns a dialect-shaped 404 — that is the
type-only impostering behavior of the shipped config, not a failure.
