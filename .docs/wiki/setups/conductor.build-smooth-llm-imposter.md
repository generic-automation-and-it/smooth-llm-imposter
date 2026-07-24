# Conductor → SmoothLlmImposter routing setup

## TL;DR

This page contains exactly two Conductor scripts for an Amazon Linux 2023 cloud snapshot:

1. The **snapshot script** installs the general CLI tooling and native Docker Engine + Compose; persists
   `DOCKER_HOST`, `OPENAI_BASE_URL`, and `ANTHROPIC_BASE_URL`; configures Codex; pulls the published
   SmoothLlmImposter image; and creates the fully configured `smooth-llm-imposter` container.
2. The **workspace setup script** restarts the Docker daemon after snapshot restoration, verifies that the
   container metadata survived, starts the existing container, and waits for the router health endpoint.

The setup works from any repository because it uses the published multi-platform image:

`ghcr.io/generic-automation-and-it/smooth-llm-imposter:latest`

It configures these OpenCode Go mappings:

| Dialect | Incoming model | OpenCode Go model |
|---|---|---|
| Anthropic | `claude-haiku-*` | `qwen3.6-plus` |
| Anthropic | `claude-opus-4-6` | `qwen3.7-plus` |
| Anthropic | `claude-opus-4-7` | `minimax-m3` |
| OpenAI | `gpt-5.4` | `kimi-k2.7-code` |
| OpenAI | `gpt-5.5` | `kimi-k3` |

> **Snapshot-secret warning:** creating the container during snapshot construction stores its upstream secret
> in Docker container metadata inside the snapshot. Supply `OPENCODE_API_KEY` through the private Conductor
> snapshot environment—never paste it into this tracked script—and only share the snapshot with people who are
> allowed to use that credential. Rotate the credential and rebuild the snapshot if either is exposed.

## Snapshot script (install, configure, and create the container)

Use this as the Conductor snapshot lifecycle script. Conductor lifecycle logs identify the image as Amazon
Linux 2023 (for example, `/home/vercel-sandbox`), so it uses DNF4 and native Docker rather than Homebrew,
Linuxbrew, or Colima.

Before building the snapshot, set `OPENCODE_API_KEY` in its private environment. The script aliases it to the
shared `OPENCODE_GO_API_KEY` name that SmoothLlmImposter resolves for both dialect-suffixed providers. The
literal value is not written into the tracked script or shell startup files, but Docker stores it in the
created container's environment metadata.

The environment setup also persists these client endpoints in both `~/.bashrc` and `~/.zshrc`:

| Variable | Value |
|---|---|
| `DOCKER_HOST` | `unix:///var/run/docker.sock` |
| `OPENAI_BASE_URL` | `http://127.0.0.1:5080/openai/v1` |
| `ANTHROPIC_BASE_URL` | `http://127.0.0.1:5080/anthropic` |

The OpenAI base includes `/v1` because OpenAI-compatible SDKs append paths such as `/responses` and
`/chat/completions`. The Anthropic base omits `/v1` because Anthropic clients append `/v1/messages`.

```bash
#!/usr/bin/env bash
set -euo pipefail

PORT="${PORT:-5080}"
IMAGE="${SMOOTH_LLM_IMAGE:-ghcr.io/generic-automation-and-it/smooth-llm-imposter:latest}"
CONTAINER_NAME="smooth-llm-imposter"
CODEX_CONFIG="$HOME/.codex/config.toml"

# Fail before installing anything if the private snapshot environment does not
# contain the upstream key. Do not replace this with a literal credential.
: "${OPENCODE_API_KEY:?Set OPENCODE_API_KEY in the private snapshot environment.}"
export OPENCODE_GO_API_KEY="${OPENCODE_GO_API_KEY:-$OPENCODE_API_KEY}"

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
OPENAI_BASE_URL_VALUE="http://127.0.0.1:$PORT/openai/v1"
ANTHROPIC_BASE_URL_VALUE="http://127.0.0.1:$PORT/anthropic"

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
  grep -Fqx "export OPENAI_BASE_URL=\"$OPENAI_BASE_URL_VALUE\"" "$shell_rc" ||
    echo "export OPENAI_BASE_URL=\"$OPENAI_BASE_URL_VALUE\"" >>"$shell_rc"
  grep -Fqx "export ANTHROPIC_BASE_URL=\"$ANTHROPIC_BASE_URL_VALUE\"" "$shell_rc" ||
    echo "export ANTHROPIC_BASE_URL=\"$ANTHROPIC_BASE_URL_VALUE\"" >>"$shell_rc"
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

echo "--- [5] Starting Docker and pulling SmoothLlmImposter ---"
if command -v systemctl >/dev/null 2>&1; then
  sudo systemctl enable --now docker || true
else
  sudo service docker start || true
fi

# The lifecycle does not run systemd as PID 1. Start dockerd directly when the
# service command did not make Docker available, then wait for its socket.
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

echo "--- [6] Configuring Codex ---"
mkdir -p "$(dirname "$CODEX_CONFIG")"
touch "$CODEX_CONFIG"
cp -p "$CODEX_CONFIG" "$CODEX_CONFIG.bak"

# Replace only Codex's selected provider and SmoothLlmImposter's own table.
# Preserve every unrelated setting, including MCP servers and RTK config.
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

echo "--- [7] Creating the configured container ---"
sudo docker rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true
sudo --preserve-env=OPENCODE_GO_API_KEY docker run -d \
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
    echo "SmoothLlmImposter is healthy and stored in the snapshot"
    exit 0
  fi
  sleep 1
done

echo "SmoothLlmImposter did not become healthy." >&2
sudo docker logs "$CONTAINER_NAME" >&2
exit 1
```

Rebuild the snapshot when `:latest`, the provider mappings, or the upstream credential should change. Because
the container already exists in the snapshot, changing `OPENCODE_API_KEY` only in a workspace does not update
its stored environment; recreate the snapshot or recreate the container to apply a new key.

## Workspace setup script (start the preserved container)

Use this minimal script as the Conductor workspace lifecycle. It does not need to reconfigure Codex, re-pull
the image, or repeat provider settings because those files and Docker container metadata are stored in the
snapshot. Background processes do not survive snapshot restoration, so it must start `dockerd` again.

```bash
#!/usr/bin/env bash
set -euo pipefail

export DOCKER_HOST="unix:///var/run/docker.sock"
CONTAINER_NAME="smooth-llm-imposter"
PORT="${PORT:-5080}"

# Background daemons do not survive snapshot restoration.
if ! sudo docker info >/dev/null 2>&1; then
  sudo nohup dockerd </dev/null >/tmp/dockerd.log 2>&1 &

  for _ in $(seq 1 30); do
    sudo docker info >/dev/null 2>&1 && break
    sleep 1
  done
fi

sudo docker info >/dev/null 2>&1 || {
  echo "Docker failed to start; inspect /tmp/dockerd.log." >&2
  exit 1
}

# Fail clearly if the snapshot did not retain /var/lib/docker and the
# configured container metadata.
sudo docker inspect "$CONTAINER_NAME" >/dev/null 2>&1 || {
  echo "$CONTAINER_NAME was not preserved in the snapshot." >&2
  exit 1
}

# The restart policy may already have started it when dockerd came up.
sudo docker start "$CONTAINER_NAME" >/dev/null

for _ in $(seq 1 30); do
  if curl -fsS "http://127.0.0.1:$PORT/health" >/dev/null 2>&1; then
    echo "SmoothLlmImposter is ready on http://127.0.0.1:$PORT"
    exit 0
  fi
  sleep 1
done

echo "SmoothLlmImposter did not become healthy." >&2
sudo docker logs "$CONTAINER_NAME" >&2
exit 1
```

If the container-inspection step fails, the snapshot did not preserve Docker's `/var/lib/docker` state; use
the longer create-on-workspace flow instead. With the working snapshot described above, the only expected
runtime work is restarting Docker and starting the preserved container.
