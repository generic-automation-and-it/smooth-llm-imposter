# Conductor → SmoothLlmImposter routing setup

## TL;DR

This page has two independent stages. **General sandbox setup** provisions a fresh Conductor cloud sandbox
with the CLI tooling every engineer/agent needs (`gh`, Docker CLI + Compose v2, the .NET 10 SDK, `opencode`,
Claude Code, Codex CLI, `pi`, and `rtk`) — it is generic to the sandbox, not specific to SmoothLlmImposter, and
is safe to run in any repo's sandbox. **Imposter setup** publishes SmoothLlmImposter from source (`Release`
config) and runs it directly with `dotnet` on `localhost:5080` with a full set of example imposter providers,
then points the Codex CLI at the router. Point any OpenAI- or Anthropic-dialect client's base URL at
`http://localhost:5080` and the router rewrites the inbound `model` only when a non-default provider has a
matching `Models[]` entry in the **`Imposter`** config section. Read the current shipped provider keys, URLs, and
defaults in
[`src/SmoothLlmImposter.Host/appsettings.json`](../../../src/SmoothLlmImposter.Host/appsettings.json); this
setup page intentionally avoids duplicating that table. With no matching model mapping, routing falls through
to the dialect default when one is configured, or returns a dialect-shaped 404 when no default exists.

The router is **stateless and key-less**: it does not capture or persist the caller's auth, and it does not run
as a container in this setup. Each provider's upstream key comes from the environment; on a matched imposter
route the inbound `Authorization` / `x-api-key` is replaced by the provider's configured key.

## General sandbox setup

Run this first in a fresh Conductor cloud sandbox (Amazon Linux 2023 / DNF4). It installs system packages, the
GitHub CLI (via GitHub's official RPM repo, since Amazon Linux 2023 ships DNF4), Docker CLI + Compose v2, starts
and verifies the local Docker daemon, installs the .NET 10 SDK, and installs the CLI agents this repo is commonly
driven by (`opencode`, Claude Code, Codex CLI, `pi`); wires their bins onto `PATH` with a couple of YOLO-mode
aliases in `~/.bashrc`; then installs `rtk` (token-optimized CLI proxy — see `RTK.md`), patches it into the shell
and Codex config non-interactively via `expect`, and disables its telemetry. It is generic to the sandbox, not to
SmoothLlmImposter — the [Imposter setup](#imposter-setup) below is what actually clones, builds, and runs the
SmoothLlmImposter Host as a sidecar for whichever repo the sandbox is for.

```bash
#!/usr/bin/env bash
set -euo pipefail

# --- [1/7] Installing System Dependencies (dnf) ---
echo "--- [1] Installing System Dependencies (dnf) ---"
sudo dnf install -y python3-pip expect python3 'dnf-command(config-manager)' docker

# --- [2/7] Installing Core CLI Tools ---
echo "--- [2] Installing Core CLI Tools ---"

# GitHub CLI: Amazon Linux 2023 uses DNF4, so add GitHub's official RPM repo first.
sudo dnf config-manager --add-repo https://cli.github.com/packages/rpm/gh-cli.repo
sudo dnf install -y gh

# Docker Compose v2: Amazon Linux 2023 has Docker, but not the Compose plugin
# package in the default repo. Install the official CLI plugin binary so
# `docker compose` works alongside `docker`.
docker_arch="$(uname -m)"
case "$docker_arch" in
  x86_64) docker_compose_arch="x86_64" ;;
  aarch64|arm64) docker_compose_arch="aarch64" ;;
  *) echo "Unsupported Docker Compose architecture: $docker_arch" >&2; exit 1 ;;
esac

sudo install -d -m 0755 /usr/local/lib/docker/cli-plugins
sudo curl -fsSL \
  "https://github.com/docker/compose/releases/latest/download/docker-compose-linux-$docker_compose_arch" \
  -o /usr/local/lib/docker/cli-plugins/docker-compose
sudo chmod +x /usr/local/lib/docker/cli-plugins/docker-compose

# Start Docker when the snapshot supports a local daemon. Keep sudo in the
# verification commands because group membership usually needs a fresh shell.
if command -v systemctl >/dev/null 2>&1; then
  sudo systemctl enable --now docker || true
else
  sudo service docker start || true
fi

if ! sudo docker info >/dev/null 2>&1; then
  sudo nohup dockerd >/tmp/dockerd.log 2>&1 &
  for _ in $(seq 1 30); do
    sudo docker info >/dev/null 2>&1 && break
    sleep 1
  done
fi

if ! sudo docker info >/dev/null 2>&1; then
  echo "Docker daemon did not become ready; see /tmp/dockerd.log when dockerd fallback was used." >&2
  exit 1
fi

sudo usermod -aG docker "$USER" || true
sudo docker --version
sudo docker compose version

curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
curl -fsSL https://opencode.ai/install | bash
curl -fsSL https://claude.ai/install.sh | bash
curl -fsSL https://chatgpt.com/codex/install.sh | sh
curl -fsSL https://pi.dev/install.sh | sh
curl -fsSL https://raw.githubusercontent.com/rtk-ai/rtk/refs/heads/master/install.sh | sh

# --- [3/7] Fixing Environment & Paths ---
echo "--- [3] Fixing Environment & Paths ---"
sudo ln -sf /usr/bin/pip3 /usr/bin/pip

sudo mkdir -p /root/.claude
sudo mkdir -p "$HOME/.claude"

{
  echo 'alias claude-yolo="claude --dangerously-skip-permissions"'
  echo 'alias opencode-yolo="opencode --auto"'
  echo 'export DOTNET_ROOT="$HOME/.dotnet"'
  echo 'export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"'
  echo 'export PATH="/usr/local/share/dotnet:$PATH"'
  echo 'export PATH="$HOME/.opencode/bin:$PATH"'
  echo 'export PATH="$HOME/.local/bin:$PATH"'
} >> "$HOME/.bashrc"

set +u
source ~/.bashrc
set -u

export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export PATH="/usr/local/share/dotnet:$PATH"
export PATH="$HOME/.opencode/bin:$PATH"
export PATH="$HOME/.local/bin:$PATH"

# --- [4/7] Configuring RTK (Automated) ---
echo "--- [4/7] Configuring RTK (Automated) ---"
expect -c "
    spawn sudo HOME=$HOME rtk init -g --auto-patch
    expect \"Patch existing\"
    send \"y\r\"
    expect eof
"

sudo HOME=$HOME rtk init -g --codex
rtk telemetry disable
```

## Imposter setup

One script, saved as `~/.local/bin/imposter-up.sh` (`chmod +x`): clones SmoothLlmImposter from GitHub into its
own cache directory if missing, publishes it (`Release` config), starts it on `localhost:5080` with a full set
of example imposter providers, and points the Codex CLI at the router. **Works from any repo's Conductor
sandbox** — the router is a sidecar for whatever project you're actually working in, so the script never
assumes the current directory is a smooth-llm-imposter checkout; it clones its own copy into
`~/.local/state/smooth-llm-imposter/src` instead (there's no GitHub Release artifact to download — only the
GHCR image is published, see [`ghcr.run-smooth-llm-imposter.md`](ghcr.run-smooth-llm-imposter.md) — so building
this "release" means `dotnet publish -c Release` from a clone, not fetching a prebuilt binary). This is a
**release-published binary run directly with `dotnet`, not a container** — .NET only binds config at process
**startup** (no hot reload), so every env setting below has to be in place *before* the Host starts, not set
against an already-running process.

Release-published output (`dotnet publish -c Release`) **excludes `appsettings.Development.json`**
(`CopyToPublishDirectory="Never"` in the csproj) — the same dev-only providers that live there
(`anthropic-personal`, `openrouter-*`, `opencode-go-*`, etc.) never ship, container or not. So every imposter
provider below — including `opencode-go-anthropic` / `opencode-go-openai`, which already exist with empty
`Models[]` in that dev-only file — is fully (re)defined here: `Dialect`, `BaseUrl`, `AuthScheme`, and (for the
OpenAI-compatible endpoint) `OpenAiUpstreamApi=chat_completions`, plus `Models[]`.

### Prerequisites / knobs

- **Hyphenated provider names aren't valid bash identifiers.** `Imposter__Providers__opencode-go-anthropic__Dialect`
  can't be `export`-ed — bash variable names can't contain `-`. The script below instead passes every
  `Imposter__Providers__<name>__*` structured override through `env VAR=value … command`, which sets the
  process's environment directly (no shell-identifier restriction), right before starting the Host.
- **`<NAME>_API_KEY`** / **`<NAME>_AUTH_TOKEN`** / **`<NAME>_AUTHORIZATION_BEARER`** (conventional, plain
  uppercase/underscore names — these *can* be `export`-ed normally) or `Imposter__Providers__<name>__Secret`
  (structured) — the upstream key for the named provider, where `<NAME>` is the uppercased provider key with
  dialect-suffixed siblings (e.g. `opencode-go-anthropic` / `opencode-go-openai`) sharing one base prefix
  (`OPENCODE_GO_API_KEY`). Which suffix wins **follows the effective auth scheme** — a `Bearer` provider prefers
  `_AUTH_TOKEN` → `_AUTHORIZATION_BEARER` → `_API_KEY`, an `ApiKey` provider prefers `_API_KEY` → `_AUTH_TOKEN`
  → `_AUTHORIZATION_BEARER` (the off-scheme vars stay fallbacks).
  **`export` real keys in your shell before running** — do **not** paste one into the script block below,
  because this file is tracked in the repo and a committed key would leak.
- **Anthropic personal-subscription providers.** If the general sandbox setup installed Claude Code, `claude
  setup-token` mints a long-lived Claude subscription token you can supply yourself as an imposter provider's
  `Secret` (env alias `<NAME>_AUTHORIZATION_BEARER`) with `AuthScheme=Bearer`. See
  [`setup.md`](../setup.md#minting-the-tokens) for the full walkthrough.
- **`ASPNETCORE_URLS`** / **`PORT`** — the script binds `http://+:$PORT` (default `5080`); override `PORT`
  before running to change both the bind address and the Codex `base_url` together.
- **`Admin__ApiKey` / `Admin__OperatorApiKey`** — only needed to use the `/admin/credentials` API. The admin key
  grants the `CredentialAdmin` role (all mutations); the operator key authenticates without it.
- **`ConnectionStrings__ImposterDb`** — only needed when credential-admin data should persist in PostgreSQL.
  Pure imposter routing needs no database. **When unset, the router uses no database** and stores
  credential-admin data in memory until restart. See
  [`credentials.admin-smooth-llm-imposter.md`](credentials.admin-smooth-llm-imposter.md).
- **To apply a provider change, restart the Host.** The "start" step no-ops once something is already listening
  on `$PORT` (so it's safe to re-run), which means editing `PROVIDER_ENV` below has no effect until the existing
  process is stopped: `pkill -f SmoothLlmImposter.Host`, then re-run `imposter-up.sh`.
- **To pick up new SmoothLlmImposter source, delete the cache.** Cloning and publishing are separate no-ops
  from starting — they only run once, the first time (`~/.local/state/smooth-llm-imposter/src` and `.../publish`).
  To pull latest and rebuild, remove both and re-run: `rm -rf ~/.local/state/smooth-llm-imposter/{src,publish}`.

### Script

```bash
#!/usr/bin/env bash
set -euo pipefail

# ──────────────────────────────────────────────────────────────────────
#  Clones + publishes SmoothLlmImposter (Release) if missing, starts it
#  on localhost:5080 with the example providers below, and points the
#  Codex CLI at the router. Works from any repo's Conductor sandbox —
#  clones its own copy rather than assuming the cwd is a
#  smooth-llm-imposter checkout. Safe to re-run — no-ops once the Host
#  is already listening. Save as ~/.local/bin/imposter-up.sh (chmod +x).
# ──────────────────────────────────────────────────────────────────────

PORT="${PORT:-5080}"
REPO_URL="${REPO_URL:-https://github.com/generic-automation-and-it/smooth-llm-imposter.git}"
STATE_DIR="$HOME/.local/state/smooth-llm-imposter"
SRC_DIR="$STATE_DIR/src"
PUBLISH_DIR="$STATE_DIR/publish"
LOG_FILE="$STATE_DIR/imposter.log"
CODEX_CONFIG="$HOME/.codex/config.toml"

mkdir -p "$STATE_DIR"

# ── Ensure the build exists — independent of whether the Host is currently
#    running, so this is always a cheap no-op after the first run, not
#    something tangled into the "start" branch below.

# Install the .NET 10 SDK if no suitable dotnet is on PATH.
if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks | grep -q '^10\.'; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$PATH"
fi

# Get the source. This has to work from *any* repo's Conductor sandbox — the
# router is a sidecar for whatever project you're actually working in, so the
# current directory is never assumed to be a smooth-llm-imposter checkout.
# There is no GitHub Release artifact to download (only the GHCR image is
# published) and no REPO_DIR to fall back to, so clone into its own cache
# dir, once.
if [ ! -d "$SRC_DIR/.git" ]; then
  git clone --depth 1 "$REPO_URL" "$SRC_DIR"
fi

# Publish once if the build is missing.
if [ ! -x "$PUBLISH_DIR/SmoothLlmImposter.Host" ]; then
  dotnet publish "$SRC_DIR/src/SmoothLlmImposter.Host" -c Release -o "$PUBLISH_DIR"
fi

# ── Ensure it's running. Everything the build needed is already done above,
#    so this branch only has to start the process.
if curl -fsS "http://localhost:$PORT/health" >/dev/null 2>&1; then
  echo "SmoothLlmImposter already up on :$PORT"
else
  # Alias your OPENCODE_API_KEY onto the conventional shared-prefix var the
  # router auto-resolves for both opencode-go-anthropic and opencode-go-openai
  # (OPENCODE_GO_API_KEY, per setup.md) — a no-op if you already export that
  # name directly.
  export OPENCODE_GO_API_KEY="${OPENCODE_GO_API_KEY:-${OPENCODE_API_KEY:-}}"

  # Provider config for this environment. Hyphenated names (opencode-go-anthropic,
  # opencode-go-openai) aren't valid bash identifiers, so these are passed to
  # `env` — not `export` — right before starting the process. Fill in your own
  # providers or replace these examples.
  PROVIDER_ENV=(
    "Imposter__Providers__opencode-go-anthropic__Dialect=anthropic"
    "Imposter__Providers__opencode-go-anthropic__BaseUrl=https://opencode.ai/zen/go"
    "Imposter__Providers__opencode-go-anthropic__AuthScheme=ApiKey"
    "Imposter__Providers__opencode-go-anthropic__Models__0__From=claude-haiku-*"
    "Imposter__Providers__opencode-go-anthropic__Models__0__To=qwen3.6-plus"
    "Imposter__Providers__opencode-go-anthropic__Models__1__From=claude-opus-4-6"
    "Imposter__Providers__opencode-go-anthropic__Models__1__To=qwen3.7-plus"
    "Imposter__Providers__opencode-go-anthropic__Models__2__From=claude-opus-4-7"
    "Imposter__Providers__opencode-go-anthropic__Models__2__To=minimax-m3"
    "Imposter__Providers__opencode-go-openai__Dialect=openai"
    "Imposter__Providers__opencode-go-openai__BaseUrl=https://opencode.ai/zen/go"
    "Imposter__Providers__opencode-go-openai__AuthScheme=Bearer"
    "Imposter__Providers__opencode-go-openai__OpenAiUpstreamApi=chat_completions"
    "Imposter__Providers__opencode-go-openai__Models__0__From=gpt-5.4"
    "Imposter__Providers__opencode-go-openai__Models__0__To=kimi-k2.7-code"
    "Imposter__Providers__opencode-go-openai__Models__1__From=gpt-5.5"
    "Imposter__Providers__opencode-go-openai__Models__1__To=kimi-k3"
  )

  # Start detached. `env` sets the array above (hyphenated names) plus the
  # plain conventional secret, all resolved before the process starts.
  env "${PROVIDER_ENV[@]}" \
    ASPNETCORE_URLS="http://+:$PORT" \
    OPENCODE_GO_API_KEY="$OPENCODE_GO_API_KEY" \
    nohup "$PUBLISH_DIR/SmoothLlmImposter.Host" >>"$LOG_FILE" 2>&1 &

  for _ in $(seq 1 30); do
    curl -fsS "http://localhost:$PORT/health" >/dev/null 2>&1 && break
    sleep 1
  done
  curl -fsS "http://localhost:$PORT/health" && echo "  <- SmoothLlmImposter is up on :$PORT"
fi

# ── Point the Codex CLI at the router (ChatGPT/subscription auth). Idempotent:
#    only appends `model_provider` / `[model_providers.smooth-llm-proxy]` when
#    they're not already present — never overwrites an existing
#    ~/.codex/config.toml, so it doesn't clobber rtk's --codex patch from
#    General sandbox setup or your own [mcp_servers.*] entries.
mkdir -p "$(dirname "$CODEX_CONFIG")"
touch "$CODEX_CONFIG"

if grep -q '^model_provider' "$CODEX_CONFIG"; then
  echo "model_provider already set — skipping."
else
  tmp=$(mktemp)
  { echo 'model_provider = "smooth-llm-proxy"'; echo; cat "$CODEX_CONFIG"; } > "$tmp"
  mv "$tmp" "$CODEX_CONFIG"
  echo "Added: model_provider"
fi

if grep -q '\[model_providers\.smooth-llm-proxy\]' "$CODEX_CONFIG"; then
  echo "[model_providers.smooth-llm-proxy] already exists — skipping."
else
  cat >> "$CODEX_CONFIG" <<TOML

[model_providers.smooth-llm-proxy]
name = "Smooth LLM Imposter"
base_url = "http://127.0.0.1:$PORT/openai"
wire_api = "responses"
requires_openai_auth = true
request_max_retries = 3
stream_max_retries = 10
stream_idle_timeout_ms = 300000
TOML
  echo "Added: [model_providers.smooth-llm-proxy]"
fi
```

Model IDs use the bare upstream string (`kimi-k2.7-code`, `minimax-m3`, `qwen3.6-plus`, `qwen3.7-plus`,
`kimi-k3`) — **no `opencode-go/` prefix** — matching the live-upstream eval
(`tests/SmoothLlmImposter.Upstream.EvalTest/OpencodeToolNormalizationEvalTests.cs`) and the [HLD 001 example
config](../../hlds/001-llm-imposter-routing/README.md#configuration), both of which have actually been run
against the real `https://opencode.ai/zen/go` upstream.

Export the secret in your shell first, then run the script:

```bash
export OPENCODE_API_KEY="paste-your-opencode-key-here"
imposter-up.sh
```

### Point Codex CLI at the router

The script above ends by writing `~/.codex/config.toml` so the Codex CLI (ChatGPT/subscription auth) drives the
router out of the box — a fresh Conductor cloud sandbox starts with no `~/.codex/config.toml` (new Linux home
directory), so the script creates it. It never overwrites an existing file — it only appends the top-level
`model_provider` key and the `[model_providers.smooth-llm-proxy]` table, and only when each is **not already
present**:

- If `model_provider` is already set (for example because [General sandbox
  setup](#general-sandbox-setup)'s `rtk init -g --codex` patched it), the script leaves it as-is and prints
  `model_provider already set — skipping.` — edit `~/.codex/config.toml` yourself to point the top-level key back
  at `"smooth-llm-proxy"` if you want the router selected.
- If `[model_providers.smooth-llm-proxy]` already exists, it's left as-is too — re-running the script never
  duplicates or clobbers the table, or any other table you've added (`[mcp_servers.*]` etc.).

The table it appends looks like:

```toml
# ~/.codex/config.toml
model_provider = "smooth-llm-proxy"

[model_providers.smooth-llm-proxy]
name = "Smooth LLM Imposter"
base_url = "http://127.0.0.1:5080/openai"
wire_api = "responses"
requires_openai_auth = true
request_max_retries = 3
stream_max_retries = 10
stream_idle_timeout_ms = 300000
```

The `base_url` is the router root **plus the `/openai` dialect prefix** — Codex's Responses-API client appends
`/responses`, and the router selects the OpenAI dialect from the prefix and forwards the rest of the path
verbatim. `request_max_retries` / `stream_max_retries` / `stream_idle_timeout_ms` tune Codex's own client-side
retry/timeout behavior against the router — a separate concern from any retry logic SmoothLlmImposter itself
applies to the upstream call.

The provider selection applies to every local Codex model request for the selected config/profile, including
models picked later with `/model`. It does not proxy Codex login, model-catalog refresh, web search, MCP servers,
connectors, or cloud tasks. Keep `wire_api = "responses"` even when a matched imposter provider's upstream speaks
`chat_completions` — Smooth converts and re-emits Responses events for Codex.

> If your only customization is MCP servers and you want it to follow you across fresh sandboxes, commit a
> project-level `.codex/config.toml` to the repo root instead: cloud workspaces clone the repo on startup, so
> it's present without any setup-script change.

### Verify routing end-to-end

After setup, from the sandbox:

```bash
curl -fsS localhost:5080/health        # {"status":"ok"}
```

Send an OpenAI-dialect request. If the model matches no configured `Models[]` entry, it uses the dialect default
when configured; to enable rewriting, add a `Models[]` entry to a non-default provider:

```bash
curl -fsS localhost:5080/v1/chat/completions \
  -H "content-type: application/json" \
  -d '{ "model": "gpt-5.4", "messages": [ { "role": "user", "content": "Say hello in one sentence." } ] }'
```

Watch the Host log for the model swap and upstream forward:

```bash
tail -f ~/.local/state/smooth-llm-imposter/imposter.log
```

A model that matches no mapping and has no configured `IsDefault` provider returns a dialect-shaped 404; that is
type-only impostering behavior, not a proxy failure.
