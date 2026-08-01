#!/usr/bin/env bash
set -euo pipefail

# Container lifecycle only. No CONDUCTOR_IS_LOCAL guard, deliberately: a manual
# trigger must always act and report. See .conductor/AGENTS.md.

PORT="${PORT:-5080}"
IMAGE="${SMOOTH_LLM_IMAGE:-ghcr.io/generic-automation-and-it/smooth-llm-imposter:latest}"
CONTAINER_NAME="smooth-llm-imposter"
DOCKERD_LOG="/tmp/dockerd.log"
# Lets diagnose() distinguish "the daemon said nothing" from "not our daemon".
DOCKERD_STARTED_BY_US=0

if ! command -v docker >/dev/null 2>&1; then
  echo "Docker is missing. Build this workspace from the documented snapshot." >&2
  exit 1
fi

# Linux-only: no systemd as PID 1, so dockerd must be started by hand after a
# restart. On macOS Docker Desktop owns it, and exporting DOCKER_HOST would
# override the docker context that resolves its socket.
if [ "$(uname -s)" = "Linux" ]; then
  export DOCKER_HOST="${DOCKER_HOST:-unix:///var/run/docker.sock}"

  # Unprivileged socket first; only need sudo if that fails.
  if ! docker info >/dev/null 2>&1; then
    if ! sudo -n true 2>/dev/null; then
      echo "sudo requires a password; configure NOPASSWD for dockerd or run interactively." >&2
      exit 1
    fi
    if ! sudo -n docker info >/dev/null 2>&1; then
      # setsid, not just nohup: nohup covers SIGHUP but not a process-group
      # teardown when the caller exits.
      sudo -n setsid nohup dockerd </dev/null >"$DOCKERD_LOG" 2>&1 &
      DOCKERD_STARTED_BY_US=1

      for _ in $(seq 1 30); do
        sudo -n docker info >/dev/null 2>&1 && break
        sleep 1
      done
    fi

    sudo -n docker info >/dev/null 2>&1 || {
      echo "Docker failed to start." >&2
      [ -s "$DOCKERD_LOG" ] && { echo "--- last 40 lines of $DOCKERD_LOG ---" >&2; tail -40 "$DOCKERD_LOG" >&2; }
      exit 1
    }
  fi
fi

# Injected by Conductor into the workspace lifecycle only.
export OPENCODE_GO_API_KEY="${OPENCODE_GO_API_KEY:-${OPENCODE_API_KEY:-}}"
: "${OPENCODE_GO_API_KEY:?Set OPENCODE_API_KEY or OPENCODE_GO_API_KEY in the workspace environment.}"
# Exported so `-e NAME` forwards the value without putting it on the command line.
export OPENROUTER_API_KEY="${OPENROUTER_API_KEY:?Set OPENROUTER_API_KEY in the workspace environment.}"
# Uncomment (plus --preserve-env and the -e flags) to stop OpenCode session
# token usage. Image default is SessionForwarding=opencode-go.
#export OPENCODE_GO_ANTHROPIC_SESSION_FORWARDING="${OPENCODE_GO_ANTHROPIC_SESSION_FORWARDING:-none}"
#export OPENCODE_GO_OPENAI_SESSION_FORWARDING="${OPENCODE_GO_OPENAI_SESSION_FORWARDING:-none}"

# Prefer unprivileged docker; through sudo, preserve the secrets by name.
if docker info >/dev/null 2>&1; then
  DOCKER=(docker)
elif sudo -n docker info >/dev/null 2>&1; then
  DOCKER=(sudo -n --preserve-env=OPENCODE_GO_API_KEY,OPENROUTER_API_KEY docker)
else
  echo "Docker is not reachable; on Linux the daemon is down, on macOS start Docker Desktop." >&2
  exit 1
fi

# Prints to the terminal, because the operator has no filesystem access.
# Daemon checked FIRST: a bare `docker logs` reports "Cannot connect to the
# Docker daemon" in exactly the case that matters, hiding the real cause.
diagnose() {
  echo "================ imposter diagnostics ================" >&2
  if "${DOCKER[@]}" info >/dev/null 2>&1; then
    echo "[daemon]    reachable" >&2
    echo "[container] $("${DOCKER[@]}" ps -a --filter "name=^/${CONTAINER_NAME}$" \
      --format '{{.Status}} | image {{.Image}}' 2>/dev/null || echo 'not found')" >&2
    echo "[container] last 100 log lines:" >&2
    "${DOCKER[@]}" logs --tail 100 "$CONTAINER_NAME" 2>&1 | sed 's/^/    /' >&2 || true
  else
    echo "[daemon]    UNREACHABLE at ${DOCKER_HOST:-the default socket}." >&2
    echo "[daemon]    The container cannot outlive its daemon, so this — not the" >&2
    echo "[daemon]    router — is the failure. Container logs are unavailable." >&2
    if pgrep -x dockerd >/dev/null 2>&1; then
      echo "[daemon]    a dockerd process exists but is not answering" >&2
    else
      echo "[daemon]    no dockerd process is running (it exited or was killed)" >&2
    fi
    if [ -s "$DOCKERD_LOG" ]; then
      echo "[daemon]    last 40 lines of $DOCKERD_LOG:" >&2
      tail -40 "$DOCKERD_LOG" | sed 's/^/    /' >&2
    elif [ "$DOCKERD_STARTED_BY_US" = "1" ]; then
      echo "[daemon]    $DOCKERD_LOG is empty — the daemon died without writing anything" >&2
    else
      echo "[daemon]    this script did not start that daemon, so $DOCKERD_LOG is not" >&2
      echo "[daemon]    its log; the sandbox boot owns it and its output is elsewhere" >&2
    fi
    echo "[kernel]    recent OOM/kill messages (empty means no OOM kill):" >&2
    { dmesg 2>/dev/null || sudo -n dmesg 2>/dev/null || true; } |
      grep -iE "out of memory|oom-kill|killed process" | tail -10 | sed 's/^/    /' >&2 || true
  fi
  echo "======================================================" >&2
}

# Pull BEFORE `docker rm -f`, and tolerate failure. Never use
# `docker run --pull=always`: it exits 125 on an unreachable registry even with
# the image cached, after the container has already been destroyed.
if ! "${DOCKER[@]}" pull "$IMAGE"; then
  echo "Image pull failed; falling back to the locally cached image." >&2
  if ! "${DOCKER[@]}" image inspect "$IMAGE" >/dev/null 2>&1; then
    echo "No local copy of $IMAGE either — leaving the existing container untouched." >&2
    diagnose
    exit 1
  fi
fi

# openrouter-* is absent from the published image, so it is defined in full here.
# Never put a "#" comment inside the backslash continuation below: it swallows
# the rest of the logical line, silently dropping "$IMAGE".
"${DOCKER[@]}" rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true
"${DOCKER[@]}" run -d \
  --name "$CONTAINER_NAME" \
  --restart unless-stopped \
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
  -e "Imposter__Providers__opencode-go-openai-chat__Dialect=openai" \
  -e "Imposter__Providers__opencode-go-openai-chat__BaseUrl=https://opencode.ai/zen/go" \
  -e "Imposter__Providers__opencode-go-openai-chat__OpenAiUpstreamApi=chat_completions" \
  -e "Imposter__Providers__opencode-go-openai-chat__AuthScheme=Bearer" \
  -e "Imposter__Providers__opencode-go-openai-chat__Models__0__From=gpt-5.4" \
  -e "Imposter__Providers__opencode-go-openai-chat__Models__0__To=kimi-k2.7-code" \
  -e "Imposter__Providers__opencode-go-openai-chat__Models__1__From=gpt-5.5" \
  -e "Imposter__Providers__opencode-go-openai-chat__Models__1__To=glm-5.2" \
  -e "Imposter__Providers__opencode-go-openai-responses__Dialect=openai" \
  -e "Imposter__Providers__opencode-go-openai-responses__BaseUrl=https://opencode.ai/zen/go" \
  -e "Imposter__Providers__opencode-go-openai-responses__OpenAiUpstreamApi=responses" \
  -e "Imposter__Providers__opencode-go-openai-responses__AuthScheme=Bearer" \
  -e "Imposter__Providers__opencode-go-openai-responses__Models__0__From=gpt-5.6-luna" \
  -e "Imposter__Providers__opencode-go-openai-responses__Models__0__To=grok-4.5" \
  -e OPENCODE_GO_API_KEY \
  -e OPENROUTER_API_KEY \
  "$IMAGE" >/dev/null

# Wait for health, tolerating a daemon that goes away mid-wait. `--restart
# unless-stopped` brings the container back once a daemon returns, so a daemon
# blip is recoverable and must not be reported as a dead router — but it does
# need saying out loud, because it is a completely different fault from the
# container failing to serve.
daemon_blipped=0
for _ in $(seq 1 60); do
  if curl -fsS "http://127.0.0.1:$PORT/health" >/dev/null 2>&1; then
    if [ "$daemon_blipped" = "1" ]; then
      echo "Note: the Docker daemon was briefly unreachable during startup; the container recovered." >&2
    fi
    echo "SmoothLlmImposter is ready on http://127.0.0.1:$PORT"
    exit 0
  fi
  if ! "${DOCKER[@]}" info >/dev/null 2>&1; then
    [ "$daemon_blipped" = "1" ] || echo "Docker daemon went unreachable while waiting for health; still waiting..." >&2
    daemon_blipped=1
  fi
  sleep 1
done

echo "SmoothLlmImposter did not become healthy after 60s." >&2
diagnose
exit 1
