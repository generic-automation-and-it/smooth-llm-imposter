#!/usr/bin/env bash
set -euo pipefail

# The container lifecycle only. Deliberately NOT gated on CONDUCTOR_IS_LOCAL:
# `restart-imposter` is an explicit manual trigger, and a trigger that exits 0
# without printing anything is worse than one that tries and fails loudly. Only
# `setup.sh` short-circuits on a local workspace, because the work it does
# beyond this script (Codex config, code-review-graph) is what must not clobber
# a local developer's machine. See `.conductor/AGENTS.md`.

PORT="${PORT:-5080}"
IMAGE="${SMOOTH_LLM_IMAGE:-ghcr.io/generic-automation-and-it/smooth-llm-imposter:latest}"
CONTAINER_NAME="smooth-llm-imposter"
DOCKERD_LOG="/tmp/dockerd.log"
# Set when this script is the one that spawned dockerd. If it is not, an empty
# or absent DOCKERD_LOG means "someone else owns that daemon", not "the daemon
# said nothing" — the diagnostics below depend on telling those two apart.
DOCKERD_STARTED_BY_US=0

if ! command -v docker >/dev/null 2>&1; then
  echo "Docker is missing. Build this workspace from the documented snapshot." >&2
  exit 1
fi

# Daemon bootstrap is Linux-only. The cloud sandbox has no systemd as PID 1, so
# dockerd does not survive snapshot restoration or a micro-VM restart and has to
# be started by hand. On macOS there is no dockerd binary — Docker Desktop owns
# the daemon and the CLI resolves its socket through the active docker context,
# which exporting DOCKER_HOST would override with a path Docker Desktop does not
# necessarily publish.
if [ "$(uname -s)" = "Linux" ]; then
  export DOCKER_HOST="${DOCKER_HOST:-unix:///var/run/docker.sock}"

  # Test the unprivileged Docker socket first; only require passwordless sudo
  # if the privileged path is actually needed.
  if ! docker info >/dev/null 2>&1; then
    if ! sudo -n true 2>/dev/null; then
      echo "sudo requires a password; configure NOPASSWD for dockerd or run interactively." >&2
      exit 1
    fi
    if ! sudo -n docker info >/dev/null 2>&1; then
      # setsid detaches the daemon into its own session, so it cannot be reaped
      # along with this script's process group when the caller (a Conductor run
      # script, or a terminal the user closes) goes away. nohup alone only
      # covers SIGHUP.
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

# Conductor injects these only into the workspace lifecycle, not snapshot
# construction. Alias OPENCODE_API_KEY to the shared prefix resolved by both
# OpenCode Go dialect providers; OPENROUTER_API_KEY feeds the OpenRouter
# Anthropic-dialect haiku route.
export OPENCODE_GO_API_KEY="${OPENCODE_GO_API_KEY:-${OPENCODE_API_KEY:-}}"
: "${OPENCODE_GO_API_KEY:?Set OPENCODE_API_KEY or OPENCODE_GO_API_KEY in the workspace environment.}"
# Export so docker `-e OPENROUTER_API_KEY` can inherit the value (name-only pass-through).
export OPENROUTER_API_KEY="${OPENROUTER_API_KEY:?Set OPENROUTER_API_KEY in the workspace environment.}"
# Image default is SessionForwarding=opencode-go on both opencode-go-* providers.
# Uncomment the exports and -e flags below to stop OpenCode session token usage
# (matched routes will no longer stamp session_id / x-opencode-session).
#export OPENCODE_GO_ANTHROPIC_SESSION_FORWARDING="${OPENCODE_GO_ANTHROPIC_SESSION_FORWARDING:-none}"
#export OPENCODE_GO_OPENAI_SESSION_FORWARDING="${OPENCODE_GO_OPENAI_SESSION_FORWARDING:-none}"

# Prefer unprivileged Docker when the snapshot's docker-group membership is
# active. Otherwise preserve the secrets through sudo so `-e NAME` remains a
# name-only pass-through and the values do not appear in the command line.
if docker info >/dev/null 2>&1; then
  DOCKER=(docker)
elif sudo -n docker info >/dev/null 2>&1; then
  DOCKER=(sudo -n --preserve-env=OPENCODE_GO_API_KEY,OPENROUTER_API_KEY docker)
else
  echo "Docker is not reachable; on Linux the daemon is down, on macOS start Docker Desktop." >&2
  exit 1
fi

# Everything this script can learn about a failure, printed to the terminal.
# The workspace it runs in is a remote cloud sandbox with no convenient
# filesystem access, so a diagnostic that only writes a file is a diagnostic
# nobody reads. Checks the daemon FIRST: `docker logs` is worthless when the
# daemon is the thing that died, and its "Cannot connect to the Docker daemon"
# error silently replaces the container logs you actually wanted.
#
# Decision tree:
#   (i) daemon up → container status + container log tail
#  (ii) daemon down → 'container logs unavailable' + dockerd process check
#       then, regardless of who started it:
#         (a) DOCKERD_LOG non-empty → tail it
#         (b) DOCKERD_LOG empty + we started it → 'died without writing'
#         (c) DOCKERD_LOG empty + we did not start it → 'sandbox boot owns the log'
#  (iii) when the daemon is down, also scan dmesg for OOM / "killed
#        process" lines (the grep matches "out of memory", "oom-kill",
#        and "killed process" — the last is intentionally broader
#        than OOM, to catch SIGKILL events that may have brought
#        dockerd down).
#
#  Reordering or renaming any of these branches will silently mis-attribute
#  the cause; keep the comment in sync with the code below.
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

# Refresh the image BEFORE touching the running container, and tolerate failure.
# `docker run --pull=always` (the previous behaviour) turns an unreachable or
# slow registry into a hard exit 125 even when the image is already cached
# locally — and by then `docker rm -f` has already destroyed the working
# container, so a transient GHCR/DNS hiccup took the router down and left
# nothing in its place. That window is widest exactly when this script is most
# likely to run: right after a micro-VM restart, while the network is still
# coming up. Pulling separately means a failed pull costs freshness, not uptime;
# `docker run` below then uses the default --pull=missing.
if ! "${DOCKER[@]}" pull "$IMAGE"; then
  echo "Image pull failed; falling back to the locally cached image." >&2
  if ! "${DOCKER[@]}" image inspect "$IMAGE" >/dev/null 2>&1; then
    echo "No local copy of $IMAGE either — leaving the existing container untouched." >&2
    diagnose
    exit 1
  fi
fi

# Create/recreate the container now that workspace secrets exist and the image
# is as fresh as the network allowed. openrouter-* is absent from the published
# base image, so define the Anthropic OpenRouter provider fully here (same
# env-var shape, but defines a new provider because the base image omits it).
#
# To stop OpenCode session token usage: uncomment the two
# OPENCODE_GO_*_SESSION_FORWARDING exports above, add
# OPENCODE_GO_ANTHROPIC_SESSION_FORWARDING and
# OPENCODE_GO_OPENAI_SESSION_FORWARDING to --preserve-env, and add
# "-e OPENCODE_GO_ANTHROPIC_SESSION_FORWARDING \" / "-e OPENCODE_GO_OPENAI_SESSION_FORWARDING \"
# below, just before "$IMAGE". Do NOT add a "#"-commented placeholder line inside
# the docker run continuation below — a "#" mid-backslash-continuation swallows
# the rest of that logical line (including any trailing "\"), which silently
# drops "$IMAGE" from the command.
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
