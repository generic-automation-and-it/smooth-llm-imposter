# Conductor → SmoothLlmImposter routing setup

## TL;DR

This page contains exactly two Conductor scripts for a Conductor cloud snapshot based on Amazon Linux 2023:

1. The **snapshot script** installs the general CLI tooling, Docker Engine + Compose; persists and loads
   `DOCKER_HOST`, `OPENAI_BASE_URL`, and `ANTHROPIC_BASE_URL`; starts the daemon so it can pre-pull the published
   SmoothLlmImposter image; and does **not** create or run the imposter container.
2. The **workspace setup script** preserves unrelated Codex configuration while selecting SmoothLlmImposter as
   its model provider, then creates the container from the image already stored in the snapshot. All provider
   configuration and secrets are passed before the container starts.

The setup is independent of the repository checked out in the workspace. It uses the published multi-platform
image:

`ghcr.io/generic-automation-and-it/smooth-llm-imposter:latest`

It configures the existing OpenCode Go providers and mappings from this guide:

| Dialect | Incoming model | OpenCode Go model |
|---|---|---|
| Anthropic | `claude-haiku-*` | `qwen3.6-plus` |
| Anthropic | `claude-opus-4-6` | `qwen3.7-plus` |
| Anthropic | `claude-opus-4-7` | `minimax-m3` |
| OpenAI | `gpt-5.4` | `kimi-k2.7-code` |
| OpenAI | `gpt-5.5` | `kimi-k3` |

The router is stateless and key-less: it does not persist the caller's authorization. Export
`OPENCODE_API_KEY` in the workspace environment before the workspace setup script runs; the script passes it
to the container under the shared provider-secret name that SmoothLlmImposter resolves for both OpenCode Go
dialects.

## Snapshot script (Amazon Linux 2023)

Use this as the Conductor snapshot setup script. Conductor lifecycle logs identify this image as Linux (for
example, the home directory is `/home/vercel-sandbox`), so use the native Amazon Linux packages rather than
Homebrew or Colima. Attempting the macOS/Homebrew path installs Linuxbrew and fails in this sandbox when the
Homebrew installer tries to use an unavailable `/dev/fd` process-substitution path.

The Docker socket is explicitly persisted in both `~/.zshrc` and `~/.bashrc` and exported in the current
snapshot process. This makes the non-interactive lifecycle and later workspace shell use the native
`/var/run/docker.sock` consistently. The published tag supports `linux/arm64` and `linux/amd64`, so Docker
selects the native image without forcing a platform.

The same environment setup points generic OpenAI and Anthropic clients at the local router. The OpenAI base
includes `/v1` because OpenAI-compatible SDKs append paths such as `/responses` and `/chat/completions`; the
Anthropic base omits `/v1` because Anthropic clients append `/v1/messages` themselves:

| Variable | Value |
|---|---|
| `OPENAI_BASE_URL` | `http://127.0.0.1:5080/openai/v1` |
| `ANTHROPIC_BASE_URL` | `http://127.0.0.1:5080/anthropic` |

The final `docker pull` warms the snapshot. There is intentionally no `docker run` in this stage.

```bash
#!/usr/bin/env bash
set -euo pipefail

IMAGE="${SMOOTH_LLM_IMAGE:-ghcr.io/generic-automation-and-it/smooth-llm-imposter:latest}"

echo "--- [1] Installing system and Docker packages ---"
sudo dnf install -y \
  git \
  python3 \
  python3-pip \
  expect \
  'dnf-command(config-manager)' \
  docker

# Amazon Linux 2023 uses DNF4, so add GitHub's official RPM repository.
sudo dnf config-manager --add-repo https://cli.github.com/packages/rpm/gh-cli.repo
sudo dnf install -y gh

# Amazon Linux 2023 does not package the Compose v2 plugin in its default
# repository. Install the official plugin for the snapshot architecture.
case "$(uname -m)" in
  x86_64) docker_compose_arch="x86_64" ;;
  aarch64|arm64) docker_compose_arch="aarch64" ;;
  *) echo "Unsupported Docker Compose architecture: $(uname -m)" >&2; exit 1 ;;
esac

sudo install -d -m 0755 /usr/local/lib/docker/cli-plugins
sudo curl -fsSL \
  "https://github.com/docker/compose/releases/latest/download/docker-compose-linux-$docker_compose_arch" \
  -o /usr/local/lib/docker/cli-plugins/docker-compose
sudo chmod +x /usr/local/lib/docker/cli-plugins/docker-compose

echo "--- [2] Installing general CLI tooling ---"
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
curl -fsSL https://opencode.ai/install | bash
curl -fsSL https://claude.ai/install.sh | bash
curl -fsSL https://chatgpt.com/codex/install.sh | sh
curl -fsSL https://pi.dev/install.sh | sh
curl -fsSL https://raw.githubusercontent.com/rtk-ai/rtk/refs/heads/master/install.sh | sh

echo "--- [3] Persisting and loading the environment ---"
DOCKER_HOST_VALUE="unix:///var/run/docker.sock"
OPENAI_BASE_URL_VALUE="http://127.0.0.1:5080/openai/v1"
ANTHROPIC_BASE_URL_VALUE="http://127.0.0.1:5080/anthropic"

for shell_rc in "$HOME/.zshrc" "$HOME/.bashrc"; do
  touch "$shell_rc"
  grep -Fqx 'alias claude-yolo="claude --dangerously-skip-permissions"' "$shell_rc" ||
    echo 'alias claude-yolo="claude --dangerously-skip-permissions"' >>"$shell_rc"
  grep -Fqx 'alias opencode-yolo="opencode --auto"' "$shell_rc" ||
    echo 'alias opencode-yolo="opencode --auto"' >>"$shell_rc"
  grep -Fqx 'export DOTNET_ROOT="$HOME/.dotnet"' "$shell_rc" ||
    echo 'export DOTNET_ROOT="$HOME/.dotnet"' >>"$shell_rc"
  grep -Fqx 'export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"' "$shell_rc" ||
    echo 'export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"' >>"$shell_rc"
  grep -Fqx 'export PATH="$HOME/.opencode/bin:$HOME/.local/bin:$PATH"' "$shell_rc" ||
    echo 'export PATH="$HOME/.opencode/bin:$HOME/.local/bin:$PATH"' >>"$shell_rc"
  grep -Fqx 'export DOCKER_HOST="unix:///var/run/docker.sock"' "$shell_rc" ||
    echo 'export DOCKER_HOST="unix:///var/run/docker.sock"' >>"$shell_rc"
  grep -Fqx 'export OPENAI_BASE_URL="http://127.0.0.1:5080/openai/v1"' "$shell_rc" ||
    echo 'export OPENAI_BASE_URL="http://127.0.0.1:5080/openai/v1"' >>"$shell_rc"
  grep -Fqx 'export ANTHROPIC_BASE_URL="http://127.0.0.1:5080/anthropic"' "$shell_rc" ||
    echo 'export ANTHROPIC_BASE_URL="http://127.0.0.1:5080/anthropic"' >>"$shell_rc"
done

export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$HOME/.opencode/bin:$HOME/.local/bin:$PATH"
export DOCKER_HOST="$DOCKER_HOST_VALUE"
export OPENAI_BASE_URL="$OPENAI_BASE_URL_VALUE"
export ANTHROPIC_BASE_URL="$ANTHROPIC_BASE_URL_VALUE"

echo "--- [4] Configuring RTK ---"
expect -c "
  spawn sudo HOME=$HOME rtk init -g --auto-patch
  expect \"Patch existing\"
  send \"y\r\"
  expect eof
"
sudo HOME="$HOME" rtk init -g --codex
rtk telemetry disable

echo "--- [5] Starting Docker and pre-pulling SmoothLlmImposter ---"
if command -v systemctl >/dev/null 2>&1; then
  sudo systemctl enable --now docker || true
else
  sudo service docker start || true
fi

# The lifecycle environment might not use systemd as PID 1. Fall back to a
# directly-started daemon and wait for its socket when the service command did
# not make Docker available.
if ! sudo docker info >/dev/null 2>&1; then
  sudo nohup dockerd </dev/null >/tmp/dockerd.log 2>&1 &
  for _ in $(seq 1 30); do
    sudo docker info >/dev/null 2>&1 && break
    sleep 1
  done
fi

if ! sudo docker info >/dev/null 2>&1; then
  echo "Docker daemon did not become ready; inspect /tmp/dockerd.log." >&2
  exit 1
fi

# New workspace processes should receive this supplementary group. Snapshot
# commands continue to use sudo because the current shell does not gain new
# group membership after usermod.
sudo usermod -aG docker "$USER" || true
sudo docker --version
sudo docker compose version
sudo docker pull "$IMAGE"

# Do not create or run the container while building the snapshot. The
# workspace setup script owns runtime configuration and container startup.
# Remove a stale container if this script is re-used to refresh a snapshot.
sudo docker rm -f smooth-llm-imposter >/dev/null 2>&1 || true
```

Rebuild the snapshot when `:latest` should advance. The workspace script deliberately uses `--pull=never`, so
every workspace made from one snapshot runs the same image that the snapshot pulled.

## Workspace setup script

Use this as the Conductor workspace setup script. Add `OPENCODE_API_KEY` to the workspace environment rather
than embedding it here.

The script performs these actions in order:

1. Loads the persisted native Docker socket and starts the Docker daemon if snapshot restoration did not keep
   it running.
2. Updates only Codex's top-level `model_provider` and
   `[model_providers.smooth-llm-proxy]` table. It backs up the existing file and preserves all unrelated
   settings, including MCP servers and RTK configuration.
3. Replaces the `smooth-llm-imposter` container and supplies both OpenCode Go providers, every model mapping,
   and the shared secret as startup environment. Configuration is bound when the Host starts; changing these
   values requires recreating the container.
4. Waits for the router health endpoint before completing.

```bash
#!/usr/bin/env bash
set -euo pipefail

PORT="${PORT:-5080}"
IMAGE="${SMOOTH_LLM_IMAGE:-ghcr.io/generic-automation-and-it/smooth-llm-imposter:latest}"
CONTAINER_NAME="smooth-llm-imposter"
CODEX_CONFIG="$HOME/.codex/config.toml"

# Conductor setup shells are not guaranteed to be interactive, so load the
# native Docker socket explicitly even though the snapshot persisted it in
# shell rc files.
export DOCKER_HOST="${DOCKER_HOST:-unix:///var/run/docker.sock}"

if ! command -v docker >/dev/null 2>&1; then
  echo "Docker is missing. Build this workspace from the documented snapshot." >&2
  exit 1
fi

# Snapshot restoration does not guarantee that background processes remain
# alive. Restart the native daemon when neither the user nor sudo can reach it.
if ! docker info >/dev/null 2>&1 && ! sudo docker info >/dev/null 2>&1; then
  if command -v systemctl >/dev/null 2>&1; then
    sudo systemctl start docker || true
  else
    sudo service docker start || true
  fi
fi

if ! docker info >/dev/null 2>&1 && ! sudo docker info >/dev/null 2>&1; then
  sudo nohup dockerd </dev/null >/tmp/dockerd.log 2>&1 &
  for _ in $(seq 1 30); do
    sudo docker info >/dev/null 2>&1 && break
    sleep 1
  done
fi

# OPENCODE_API_KEY is the user-facing workspace variable. SmoothLlmImposter
# resolves the two dialect-suffixed providers through their shared
# OPENCODE_GO_API_KEY prefix.
export OPENCODE_GO_API_KEY="${OPENCODE_GO_API_KEY:-${OPENCODE_API_KEY:-}}"
: "${OPENCODE_GO_API_KEY:?Set OPENCODE_API_KEY in the Conductor workspace environment.}"

# Prefer unprivileged Docker after snapshot user/group restoration. If the
# current process does not yet have the docker group, preserve the secret
# through sudo so `-e OPENCODE_GO_API_KEY` remains a name-only pass-through.
if docker info >/dev/null 2>&1; then
  DOCKER=(docker)
elif sudo docker info >/dev/null 2>&1; then
  DOCKER=(sudo --preserve-env=OPENCODE_GO_API_KEY docker)
else
  echo "Docker daemon did not become ready; inspect /tmp/dockerd.log." >&2
  exit 1
fi

# Configure Codex first. Replace only the selected provider and its own table;
# preserve every unrelated option already present in config.toml.
mkdir -p "$(dirname "$CODEX_CONFIG")"
touch "$CODEX_CONFIG"
cp -p "$CODEX_CONFIG" "$CODEX_CONFIG.bak"

python3 - "$CODEX_CONFIG" "$PORT" <<'PY'
from pathlib import Path
import re
import sys

config_path = Path(sys.argv[1])
port = sys.argv[2]
text = config_path.read_text()

provider_line = 'model_provider = "smooth-llm-proxy"'
first_table = re.search(r"(?m)^\[", text)
prefix_end = first_table.start() if first_table else len(text)
prefix = text[:prefix_end]
suffix = text[prefix_end:]

if re.search(r"(?m)^model_provider\s*=", prefix):
    prefix = re.sub(
        r'(?m)^model_provider\s*=.*$',
        provider_line,
        prefix,
        count=1,
    )
else:
    prefix = f"{provider_line}\n\n{prefix}"

text = prefix + suffix
smooth_table = f"""[model_providers.smooth-llm-proxy]
name = "Smooth LLM Imposter"
base_url = "http://127.0.0.1:{port}/openai"
wire_api = "responses"
requires_openai_auth = true
request_max_retries = 3
stream_max_retries = 10
stream_idle_timeout_ms = 300000
"""

table_pattern = re.compile(
    r"(?ms)^\[model_providers\.smooth-llm-proxy\]\s*\n.*?(?=^\[|\Z)"
)
if table_pattern.search(text):
    text = table_pattern.sub(smooth_table + "\n", text, count=1)
else:
    text = text.rstrip() + "\n\n" + smooth_table

config_path.write_text(text)
PY

# Recreate the container so provider settings are present before the Host binds
# configuration. The API key is passed by variable name, not expanded into the
# command line.
"${DOCKER[@]}" rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true
"${DOCKER[@]}" run -d \
  --name "$CONTAINER_NAME" \
  --restart unless-stopped \
  --pull=never \
  -p "127.0.0.1:${PORT}:5080" \
  -e "Imposter__Providers__opencode-go-anthropic__Dialect=anthropic" \
  -e "Imposter__Providers__opencode-go-anthropic__BaseUrl=https://opencode.ai/zen/go" \
  -e "Imposter__Providers__opencode-go-anthropic__AuthScheme=ApiKey" \
  -e "Imposter__Providers__opencode-go-anthropic__Models__0__From=claude-haiku-*" \
  -e "Imposter__Providers__opencode-go-anthropic__Models__0__To=qwen3.6-plus" \
  -e "Imposter__Providers__opencode-go-anthropic__Models__1__From=claude-opus-4-6" \
  -e "Imposter__Providers__opencode-go-anthropic__Models__1__To=qwen3.7-plus" \
  -e "Imposter__Providers__opencode-go-anthropic__Models__2__From=claude-opus-4-7" \
  -e "Imposter__Providers__opencode-go-anthropic__Models__2__To=minimax-m3" \
  -e "Imposter__Providers__opencode-go-openai__Dialect=openai" \
  -e "Imposter__Providers__opencode-go-openai__BaseUrl=https://opencode.ai/zen/go" \
  -e "Imposter__Providers__opencode-go-openai__AuthScheme=Bearer" \
  -e "Imposter__Providers__opencode-go-openai__OpenAiUpstreamApi=chat_completions" \
  -e "Imposter__Providers__opencode-go-openai__Models__0__From=gpt-5.4" \
  -e "Imposter__Providers__opencode-go-openai__Models__0__To=kimi-k2.7-code" \
  -e "Imposter__Providers__opencode-go-openai__Models__1__From=gpt-5.5" \
  -e "Imposter__Providers__opencode-go-openai__Models__1__To=kimi-k3" \
  -e OPENCODE_GO_API_KEY \
  "$IMAGE" >/dev/null

for _ in $(seq 1 30); do
  if curl -fsS "http://127.0.0.1:$PORT/health" >/dev/null 2>&1; then
    echo "SmoothLlmImposter is healthy on http://127.0.0.1:$PORT"
    exit 0
  fi
  sleep 1
done

echo "SmoothLlmImposter did not become healthy." >&2
"${DOCKER[@]}" logs "$CONTAINER_NAME" >&2
exit 1
```

Codex sends Responses API traffic to `http://127.0.0.1:5080/openai/responses`. SmoothLlmImposter selects the
OpenAI dialect from the `/openai` prefix, rewrites the configured model, converts the request when the matched
provider uses `chat_completions`, and forwards it to OpenCode Go.

To inspect the running service, use `curl -fsS http://127.0.0.1:5080/health` or
`docker logs -f smooth-llm-imposter` (`sudo docker ...` if the current shell has not acquired the snapshot's
Docker group). A missing `OPENCODE_API_KEY`, missing snapshotted image, unavailable Docker daemon, or failed
health check makes the workspace setup fail instead of leaving Codex pointed at a silently broken router.
