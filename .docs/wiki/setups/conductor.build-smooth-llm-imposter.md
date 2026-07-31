# Conductor → SmoothLlmImposter routing setup

## TL;DR

This page covers two Conductor script roles (snapshot and workspace) plus their shared source of truth in `.conductor/`:

1. The **snapshot script** installs the general CLI tooling (including GitHub Copilot CLI, `uv`, and
   `code-review-graph`) and native Docker Engine + Compose; persists `DOCKER_HOST`, `OPENAI_BASE_URL`, and
   `ANTHROPIC_BASE_URL`; configures Codex; pulls the published SmoothLlmImposter image; and does not require
   provider credentials.
2. The **workspace setup script** restarts the Docker daemon after snapshot restoration, wires
   `code-review-graph` into Codex, Copilot CLI, OpenCode, and Claude Code and builds the graph for the
   checked-out repository, reads
   `OPENCODE_API_KEY` and `OPENROUTER_API_KEY` from the workspace environment, creates the configured container
   from the already-pulled image, and waits for the router health endpoint. It is also available as a shared,
   committed Conductor script (`.conductor/settings.toml` + `.conductor/scripts/`) — see
   [Shared Conductor script](#shared-conductor-script-recommended-over-the-manual-paste-in-above) — plus an
   on-demand `restart-imposter` trigger to recreate the container without recreating the workspace.

The setup works from any repository because it uses the published multi-platform image:

`ghcr.io/generic-automation-and-it/smooth-llm-imposter:latest`

It configures these imposter mappings:

| Dialect | Incoming model | Upstream provider | Upstream model |
|---|---|---|---|
| Anthropic | `claude-sonnet-4-6` | OpenCode Go | `qwen3.6-plus` |
| Anthropic | `claude-opus-4-6` | OpenCode Go | `qwen3.7-plus` |
| Anthropic | `claude-opus-4-8` | OpenCode Go | `qwen3.7-max` |
| Anthropic | `claude-haiku-*` | OpenRouter | `inclusionai/ling-3.0-flash:free` |
| OpenAI | `gpt-5.4` | OpenCode Go | `kimi-k2.7-code` |
| OpenAI | `gpt-5.5` | OpenCode Go | `glm-5.2` |
| OpenAI | `gpt-5.6-luna` | OpenCode Go | `grok-4.5` |

These are setup-specific mappings chosen for this Conductor environment. They intentionally differ from the
illustrative mappings and caching choices in
[HLD 001](../../hlds/001-llm-imposter-routing/README.md#configuration); the HLD is not the runtime source of
truth for this script. OpenCode Go target IDs are bare upstream strings with no `opencode-go/` prefix,
consistent with the live-upstream
[`OpencodeToolNormalizationEvalTests.cs`](../../../tests/SmoothLlmImposter.Upstream.EvalTest/OpencodeToolNormalizationEvalTests.cs).
OpenRouter targets keep the provider-prefixed slug the OpenRouter API expects (here `inclusionai/ling-3.0-flash:free`).

Inbound API model names (the `From` column above) are imposter-side aliases — they are what
clients send to the proxy. The `To` column names the upstream wire ID, which is the identifier
the upstream provider uses on its own API. For the OpenAI row `gpt-5.6-luna → grok-4.5`, the imposter
accepts the OpenAI-style alias and forwards to xAI; see
[OpenAI's model index](https://platform.openai.com/docs/models) for the imposter-side namespace
and [xAI's model index](https://docs.x.ai/docs/models) for the upstream wire ID.

The image default enables session identity forwarding (`SessionForwarding: opencode-go`) for the OpenCode Go
providers, so matched routes stamp `session_id` and `x-opencode-session`. The shared workspace script uses this
default as-is. To stop OpenCode session token usage, uncomment the two exports, add both names to
`--preserve-env`, and add the two `-e` flags to the `docker run`.

## Snapshot script (install, configure, and pull the image)

Use this as the Conductor snapshot lifecycle script. Conductor lifecycle logs identify the image as Amazon
Linux 2023 (for example, `/home/vercel-sandbox`), so it uses DNF4 and native Docker rather than Homebrew,
Linuxbrew, or Colima.

Provider credentials are intentionally absent from snapshot construction: Conductor makes
`OPENCODE_API_KEY` and `OPENROUTER_API_KEY` available only to the later workspace lifecycle. The snapshot
therefore performs every credential-independent operation—including Codex configuration and the image
pull—but does not create the container.

The environment setup also persists these client endpoints in both `~/.bashrc` and `~/.zshrc`:

| Variable | Value |
|---|---|
| `DOCKER_HOST` | `unix:///var/run/docker.sock` |
| `OPENAI_BASE_URL` | `http://127.0.0.1:5080/openai/v1` |
| `ANTHROPIC_BASE_URL` | `http://127.0.0.1:5080/anthropic` |

The OpenAI base includes `/v1` because OpenAI-compatible SDKs append paths such as `/responses` and
`/chat/completions`. The Anthropic base omits `/v1` because Anthropic clients append `/v1/messages`.

If adding a Claude personal-subscription provider, `claude setup-token` can mint the subscription token to
supply explicitly as that provider's `Secret` with the matching `AuthScheme`. See
[`setup.md` → Minting the tokens](../setup.md#minting-the-tokens).

> **`code-review-graph` behavior.** [`code-review-graph`](https://github.com/tirth8205/code-review-graph) parses
> the repository with Tree-sitter into a local SQLite graph under `.code-review-graph/` and serves it to agents
> over MCP. Two constraints shape where its steps run:
>
> - **Install it into a virtualenv, not `pip install --user`.** The tool validates each Tree-sitter grammar by
>   loading it in a subprocess started with `python -I`. Isolated mode removes the per-user site-packages
>   directory from `sys.path`, so under a `--user` install every grammar probe fails, `build` skips all files,
>   and it still exits 0 — reporting success while producing a graph with zero nodes and zero edges. Verified on
>   this repository: the `--user` install yielded `0 nodes, 0 edges`; the venv install yielded `1313 nodes,
>   6650 edges` with C# parsed. `pipx` also works (it uses venvs internally) but is absent from the Amazon
>   Linux 2023 repositories.
> - **`install` and `build` are repository-scoped, so they belong to the workspace lifecycle.** `install` records
>   an absolute repo path as the MCP server's `cwd`, and `build` writes into the working tree; neither is
>   meaningful in the snapshot, which has no clone. Only the Python environment itself is snapshot-stable.
> - **The generated MCP configs invoke `uvx`, not the venv entry point.** Every platform's config runs
>   `uvx code-review-graph serve`, regardless of how the tool itself was installed. The snapshot's venv plus
>   `/usr/local/bin` symlink therefore only satisfies `build`; without `uv` on PATH the MCP server cannot
>   start and agents see no graph at all, while `build` still reports success. This is why step [2] installs
>   `uv`.
>
> The workspace configures four platforms: `codex`, `copilot-cli`, `opencode`, and `claude-code`.
>
> **`--no-instructions` is mandatory on all four.** By default `install` appends a ~39-line MCP-tools section to
> `CLAUDE.md`, which in this repository is a committed symlink to `AGENTS.md` — so the append lands in the root
> context file. Today it stays dormant only because the lifecycle has no TTY and the confirmation prompt
> defaults to "no"; the flag makes that independent of TTY allocation. `-y` is paired with it to guarantee the
> step never blocks on a prompt.
>
> **`claude-code` needs `--no-skills --no-hooks` on top.** Its skills and hooks resolve through the committed
> `.claude -> .agents` symlink into `.agents/skills/` (81 tracked files) and `.agents/settings.json`. With all
> three flags it writes only `.mcp.json`. The other three keep the defaults, because their hooks and plugins
> land under `$HOME` (`~/.codex/hooks.json`, `~/.config/opencode/plugins/crg-plugin.ts`) rather than in the
> working tree.
>
> Config scope differs by platform and determines what shows up in a workspace diff. `codex` and `copilot-cli`
> are user-scoped (`~/.codex/config.toml`, `~/.copilot/mcp-config.json`); `claude-code` and `opencode` are
> project-scoped (`.mcp.json`, `opencode.jsonc` at the repo root). The script adds the project-scoped pair to
> `.git/info/exclude` — repo-local and itself untracked, so it hides them without editing the tracked
> `.gitignore`. One residue remains: `install` appends `.code-review-graph/` to `.gitignore` by grepping that
> file directly rather than consulting `git check-ignore`, so every workspace still carries that one-line
> modification. That behavior predates this configuration and applies to all platforms.
>
> The steps are non-fatal (`|| true`) so a code-intelligence failure never blocks router startup. `install` also
> writes a `.git/hooks/pre-commit` hook that refreshes the graph; it lives inside `.git`, so it never appears in
> a diff, but it does run on every commit made in the workspace.

```bash
#!/usr/bin/env bash
set -euo pipefail

PORT="${PORT:-5080}"
IMAGE="${SMOOTH_LLM_IMAGE:-ghcr.io/generic-automation-and-it/smooth-llm-imposter:latest}"

echo "--- [1] Installing system and Docker packages ---"
sudo dnf install -y \
  git \
  python3 \
  python3-pip \
  python3.12 \
  python3.12-pip \
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
# Installs to ~/.local/bin, which the PATH exports below already cover.
curl -fsSL https://gh.io/copilot-install | bash

# uv also lands in ~/.local/bin. UV_NO_MODIFY_PATH keeps step [3] the single
# owner of the shell rc files; without it the installer appends its own PATH
# line to .bashrc/.zshrc. The variable must be set on `sh` (via `env`), not as a
# prefix on `curl` — a prefix would only reach the left side of the pipe.
curl -LsSf https://astral.sh/uv/install.sh | env UV_NO_MODIFY_PATH=1 sh

# code-review-graph needs Python >= 3.10; Amazon Linux 2023's default python3 is
# 3.9, so build a dedicated 3.12 virtualenv. A venv (not `pip install --user`) is
# required: the tool probes each Tree-sitter grammar with `python -I`, and
# isolated mode drops the per-user site-packages directory from sys.path. Under a
# --user install every grammar probe fails, so `build` skips all files and still
# exits 0 — a silent empty graph. Symlink the entry point onto PATH so the
# workspace lifecycle and interactive shells resolve it without activation.
python3.12 -m venv "$HOME/.local/crg"
"$HOME/.local/crg/bin/pip" install --quiet --upgrade pip
"$HOME/.local/crg/bin/pip" install --quiet code-review-graph
sudo ln -sf "$HOME/.local/crg/bin/code-review-graph" /usr/local/bin/code-review-graph

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

# Container creation belongs to the later credential-aware workspace lifecycle.
# Keep only the image in the snapshot.
sudo docker rm -f smooth-llm-imposter >/dev/null 2>&1 || true
```

> **Image pull behavior.** Workspace startup uses `--pull=always`, so Docker checks GHCR for a newer
> `smooth-llm-imposter:latest` on every container start and pulls it if available. Provider mappings and
> credentials are bound when the workspace creates the container, so those can change independently of the
> image.

## Workspace setup script (create and start the container)

Use this as the Conductor workspace lifecycle. `setup.sh` configures Codex (writes the
`[model_providers.smooth-llm-proxy]` table and `model_provider` value into `~/.codex/config.toml`,
preserving unrelated settings like MCP servers and RTK config — the previous file is backed up to
`~/.codex/config.toml.bak`), runs code-review-graph wiring, then delegates to `imposter-container.sh`
which restarts `dockerd`, requires the workspace-only `OPENCODE_API_KEY` and `OPENROUTER_API_KEY`,
supplies the provider mappings and secrets while creating the container, and checks GHCR for a newer
image via `--pull=always`.

```bash
#!/usr/bin/env bash
set -euo pipefail

PORT="${PORT:-5080}"
IMAGE="${SMOOTH_LLM_IMAGE:-ghcr.io/generic-automation-and-it/smooth-llm-imposter:latest}"
CONTAINER_NAME="smooth-llm-imposter"
export DOCKER_HOST="${DOCKER_HOST:-unix:///var/run/docker.sock}"

if ! command -v docker >/dev/null 2>&1; then
  echo "Docker is missing. Build this workspace from the documented snapshot." >&2
  exit 1
fi

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

# code-review-graph is installed in the snapshot, but both `install` and `build`
# are repository-scoped: `install` writes a repo-pinned `cwd` into each MCP
# config, and `build` writes the graph into the working tree. The clone only
# exists in the workspace, so run both here rather than during snapshot
# construction.
#
# --no-instructions is mandatory on every platform. Without it `install` appends
# a ~39-line MCP-tools section to CLAUDE.md, which in this repository is a
# committed symlink to AGENTS.md — the append lands in the root context file.
# It currently stays dormant only because the lifecycle has no TTY and the
# confirmation prompt defaults to "no"; the flag makes that TTY-independent.
#
# claude-code needs two more guards. Its skills and hooks resolve through the
# committed `.claude -> .agents` symlink into `.agents/skills/` (81 tracked
# files) and `.agents/settings.json`. codex, copilot-cli, and opencode write
# their hooks and plugins under $HOME instead, so they keep those defaults.
if command -v code-review-graph >/dev/null 2>&1 && git -C . rev-parse --git-dir >/dev/null 2>&1; then
  # Repo-local and untracked, so these never appear in a workspace diff and,
  # unlike .gitignore, the file itself is not under version control.
  for generated in .code-review-graph/ .mcp.json opencode.jsonc; do
    grep -Fqx "$generated" .git/info/exclude 2>/dev/null ||
      echo "$generated" >>.git/info/exclude
  done

  code-review-graph install --platform codex       -y --no-instructions || true
  code-review-graph install --platform copilot-cli -y --no-instructions || true
  code-review-graph install --platform opencode    -y --no-instructions || true
  code-review-graph install --platform claude-code -y --no-instructions \
    --no-skills --no-hooks || true
  code-review-graph build || true
else
  echo "Skipping code-review-graph setup (tool missing or not a git worktree)." >&2
fi

# Conductor injects these only into the workspace lifecycle, not snapshot
# construction. Fall back from OPENCODE_GO_API_KEY to OPENCODE_API_KEY so either
# name supplies the shared OpenCode Go key; OPENROUTER_API_KEY feeds the
# OpenRouter Anthropic-dialect haiku route.
export OPENCODE_GO_API_KEY="${OPENCODE_GO_API_KEY:-${OPENCODE_API_KEY:-}}"
: "${OPENCODE_GO_API_KEY:?Set OPENCODE_API_KEY in the workspace environment.}"
# Export so docker `-e OPENROUTER_API_KEY` can inherit the value (name-only pass-through).
export OPENROUTER_API_KEY="${OPENROUTER_API_KEY:?Set OPENROUTER_API_KEY in the workspace environment.}"
# Uncomment to stop OpenCode session token usage (routes will no longer stamp session_id / x-opencode-session):
#export OPENCODE_GO_ANTHROPIC_SESSION_FORWARDING="${OPENCODE_GO_ANTHROPIC_SESSION_FORWARDING:-none}"
#export OPENCODE_GO_OPENAI_SESSION_FORWARDING="${OPENCODE_GO_OPENAI_SESSION_FORWARDING:-none}"

# Prefer unprivileged Docker when the snapshot's docker-group membership is
# active. Otherwise preserve the secrets through sudo so `-e NAME` remains a
# name-only pass-through and the values do not appear in the command line.
if docker info >/dev/null 2>&1; then
  DOCKER=(docker)
elif sudo docker info >/dev/null 2>&1; then
  DOCKER=(sudo --preserve-env=OPENCODE_GO_API_KEY,OPENROUTER_API_KEY docker)
else
  echo "Docker failed to start; inspect /tmp/dockerd.log." >&2
  exit 1
fi

# Create the container only now, after the workspace secrets exist. The image
# was pulled into the snapshot, so do not contact GHCR during workspace setup.
# openrouter-* is absent from the published base image, so define the Anthropic
# OpenRouter provider fully here (same env-var shape, but defines a new provider
# because the base image omits it).
#
"${DOCKER[@]}" rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true
"${DOCKER[@]}" run -d \
  --name "$CONTAINER_NAME" \
  --restart unless-stopped \
  --pull=always \
  -p "127.0.0.1:${PORT}:5080" \
  -e "Imposter__Providers__opencode-go-anthropic__Dialect=anthropic" \
  -e "Imposter__Providers__opencode-go-anthropic__BaseUrl=https://opencode.ai/zen/go" \
  -e "Imposter__Providers__opencode-go-anthropic__AuthScheme=ApiKey" \
  -e "Imposter__Providers__opencode-go-anthropic__Models__0__From=claude-sonnet-4-6" \
  -e "Imposter__Providers__opencode-go-anthropic__Models__0__To=qwen3.6-plus" \
  -e "Imposter__Providers__opencode-go-anthropic__Models__1__From=claude-opus-4-6" \
  -e "Imposter__Providers__opencode-go-anthropic__Models__1__To=qwen3.7-plus" \
  -e "Imposter__Providers__opencode-go-anthropic__Models__2__From=claude-opus-4-8" \
  -e "Imposter__Providers__opencode-go-anthropic__Models__2__To=qwen3.7-max" \
  -e "Imposter__Providers__openrouter-anthropic__Dialect=anthropic" \
  -e "Imposter__Providers__openrouter-anthropic__BaseUrl=https://openrouter.ai/api" \
  -e "Imposter__Providers__openrouter-anthropic__AuthScheme=ApiKey" \
  -e "Imposter__Providers__openrouter-anthropic__Models__0__From=claude-haiku-*" \
  -e "Imposter__Providers__openrouter-anthropic__Models__0__To=inclusionai/ling-3.0-flash:free" \
  -e "Imposter__Providers__opencode-go-openai__Dialect=openai" \
  -e "Imposter__Providers__opencode-go-openai__BaseUrl=https://opencode.ai/zen/go" \
  -e "Imposter__Providers__opencode-go-openai__OpenAiUpstreamApi=responses" \
  -e "Imposter__Providers__opencode-go-openai__AuthScheme=Bearer" \
  -e "Imposter__Providers__opencode-go-openai__Models__0__From=gpt-5.4" \
  -e "Imposter__Providers__opencode-go-openai__Models__0__To=kimi-k2.7-code" \
  -e "Imposter__Providers__opencode-go-openai__Models__1__From=gpt-5.5" \
  -e "Imposter__Providers__opencode-go-openai__Models__1__To=glm-5.2" \
  -e "Imposter__Providers__opencode-go-openai__Models__2__From=gpt-5.6-luna" \
  -e "Imposter__Providers__opencode-go-openai__Models__2__To=grok-4.5" \
  -e OPENCODE_GO_API_KEY \
  -e OPENROUTER_API_KEY \
  "$IMAGE" >/dev/null
  # Uncomment below to stop OpenCode session token usage:
  # -e OPENCODE_GO_ANTHROPIC_SESSION_FORWARDING
  # -e OPENCODE_GO_OPENAI_SESSION_FORWARDING

for _ in $(seq 1 30); do
  if curl -fsS "http://127.0.0.1:$PORT/health" >/dev/null 2>&1; then
    echo "SmoothLlmImposter is ready on http://127.0.0.1:$PORT"
    exit 0
  fi
  sleep 1
done

echo "SmoothLlmImposter did not become healthy." >&2
"${DOCKER[@]}" logs "$CONTAINER_NAME" >&2
exit 1
```

The workspace must expose `OPENCODE_API_KEY` and `OPENROUTER_API_KEY`; no provider secret is required or
expected while constructing the snapshot. Re-running the workspace script recreates the container so current
provider settings and the current workspace secrets always take effect.

> **The `docker run` invocation above is identical to `.conductor/scripts/imposter-container.sh`.**
> Use the shared script for any non-exploratory setup; the manual copy is shown only
> to explain each flag. **Do not** diverge from the shared script — it is the source of truth.

## Shared Conductor script (recommended over the manual paste-in above)

The workspace script above only reaches teammates who manually paste it into their own Conductor UI, because
`.conductor/settings.local.toml` — where the Mac app and cloud setup persist a manually-configured workspace
script — is machine-local and gitignored. The same logic is also available as a **shared, committed**
Conductor script, so pulling this branch is enough; see
[Conductor's docs on sharing scripts with teammates](https://www.conductor.build/docs/reference/scripts/share-with-teammates).

| File | Role |
|---|---|
| `.conductor/settings.toml` | Committed. Points `[scripts] setup` and `[scripts.run.restart-imposter]` at the files below. `run_mode = "nonconcurrent"`, because the container uses a fixed name and a fixed host port (`127.0.0.1:5080` by default) — two workspaces racing setup or restart at once would collide over both. |
| `.conductor/scripts/imposter-container.sh` | The container lifecycle only: ensure the Docker daemon, validate `OPENCODE_API_KEY`/`OPENROUTER_API_KEY`, `docker rm -f` + `docker run -d` with the full provider mapping, wait for `/health`. Shared by both scripts below so the `docker run` invocation exists in exactly one place. |
| `.conductor/scripts/setup.sh` | The `[scripts] setup` entrypoint — code-review-graph wiring (see below) followed by `imposter-container.sh`. Runs once when a workspace is created. |
| `.conductor/scripts/restart-imposter.sh` | The `[scripts.run.restart-imposter]` entrypoint — just `imposter-container.sh`, no code-review-graph step. An on-demand trigger, runnable anytime without recreating the workspace: after pulling a new image tag, rotating `OPENCODE_API_KEY`/`OPENROUTER_API_KEY`, or recovering a crash-looped container. |

This only covers the **workspace** script. The **snapshot** script (installing Docker/dotnet/`uv`/etc., image-level)
has no `.conductor/settings.toml` equivalent — Conductor snapshots are cloud-environment configuration, not a
repository setting — so it stays a manually-pasted UI field, documented as the snapshot script above.

`setup.sh` (see `.conductor/scripts/setup.sh`) installs the four platforms with
`--no-instructions` on all of them and `--no-skills --no-hooks` on
`claude-code` only. The two project-scoped configs (`.mcp.json`,
`opencode.jsonc`) plus the `.code-review-graph/` directory are seeded into
`.git/info/exclude` so they never appear as untracked paths. Note:
`code-review-graph install` still appends one `.code-review-graph/` line to the
tracked `.gitignore` directly (predates this script, applies to all platforms)
— `.git/info/exclude` wins precedence, so the resulting behavior is correct,
but the `.gitignore` modification is expected to show up as a diff in every
workspace. See the source for the exact `install` flags.
