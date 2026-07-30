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

# Background daemons do not survive snapshot restoration or workspace resume.
# Test the unprivileged Docker socket first; only require passwordless sudo
# if the privileged path is actually needed.
if ! docker info >/dev/null 2>&1; then
  if ! sudo -n true 2>/dev/null; then
    echo "sudo requires a password; configure NOPASSWD for dockerd or run interactively." >&2
    exit 1
  fi
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
# Disable per-provider so matched routes do not stamp session_id / x-opencode-session.
# (Per-provider vars — there is no shared prefix fallback for non-Secret fields.)
export OPENCODE_GO_ANTHROPIC_SESSION_FORWARDING="${OPENCODE_GO_ANTHROPIC_SESSION_FORWARDING:-none}"
export OPENCODE_GO_OPENAI_SESSION_FORWARDING="${OPENCODE_GO_OPENAI_SESSION_FORWARDING:-none}"

# Prefer unprivileged Docker when the snapshot's docker-group membership is
# active. Otherwise preserve the secrets through sudo so `-e NAME` remains a
# name-only pass-through and the values do not appear in the command line.
if docker info >/dev/null 2>&1; then
  DOCKER=(docker)
elif sudo docker info >/dev/null 2>&1; then
  DOCKER=(sudo --preserve-env=OPENCODE_GO_API_KEY,OPENCODE_GO_ANTHROPIC_SESSION_FORWARDING,OPENCODE_GO_OPENAI_SESSION_FORWARDING,OPENROUTER_API_KEY docker)
else
  echo "Docker failed to start; inspect /tmp/dockerd.log." >&2
  exit 1
fi

# Create/recreate the container now that workspace secrets exist. The image
# was pulled into the snapshot, so do not contact GHCR here. openrouter-* is
# absent from the published base image, so define the Anthropic OpenRouter
# provider fully here (same env-var shape, but defines a new provider because
# the base image omits it).
"${DOCKER[@]}" rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true
"${DOCKER[@]}" run -d \
  --name "$CONTAINER_NAME" \
  --restart unless-stopped \
  --pull=never \
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
  -e "Imposter__Providers__openrouter-anthropic__Models__0__To=tencent/hy3" \
  -e "Imposter__Providers__opencode-go-openai__Dialect=openai" \
  -e "Imposter__Providers__opencode-go-openai__BaseUrl=https://opencode.ai/zen/go" \
  -e "Imposter__Providers__opencode-go-openai__AuthScheme=Bearer" \
  -e "Imposter__Providers__opencode-go-openai__OpenAiUpstreamApi=chat_completions" \
  -e "Imposter__Providers__opencode-go-openai__Models__0__From=gpt-5.4" \
  -e "Imposter__Providers__opencode-go-openai__Models__0__To=kimi-k2.7-code" \
  -e "Imposter__Providers__opencode-go-openai__Models__1__From=gpt-5.5" \
  -e "Imposter__Providers__opencode-go-openai__Models__1__To=glm-5.2" \
  -e "Imposter__Providers__opencode-go-openai__Models__2__From=gpt-5.6-luna" \
  -e "Imposter__Providers__opencode-go-openai__Models__2__To=grok-4.5" \
  -e OPENCODE_GO_API_KEY \
  -e OPENROUTER_API_KEY \
  -e OPENCODE_GO_ANTHROPIC_SESSION_FORWARDING \
  -e OPENCODE_GO_OPENAI_SESSION_FORWARDING \
  "$IMAGE" >/dev/null

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
