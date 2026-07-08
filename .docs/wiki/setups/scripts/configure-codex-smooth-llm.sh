#!/usr/bin/env bash
# Adds the SmoothLlmImposter provider to ~/.codex/config.toml (idempotent).
#
# Safe to run multiple times — each block is only written if not already present.
#
# Usage:
#   chmod +x configure-codex-smooth-llm.sh
#   cp configure-codex-smooth-llm.sh ~/.local/bin/
#   configure-codex-smooth-llm.sh
set -euo pipefail

CONFIG="$HOME/.codex/config.toml"
mkdir -p "$(dirname "$CONFIG")"
touch "$CONFIG"

# Add top-level model_provider if not already present
if grep -q '^model_provider' "$CONFIG"; then
  echo "model_provider already set — skipping."
else
  tmp=$(mktemp)
  { echo 'model_provider = "smooth-llm-proxy"'; echo; cat "$CONFIG"; } > "$tmp"
  mv "$tmp" "$CONFIG"
  echo "Added: model_provider"
fi

# Add [model_providers.smooth-llm-proxy] section if not already present
if grep -q '\[model_providers\.smooth-llm-proxy\]' "$CONFIG"; then
  echo "[model_providers.smooth-llm-proxy] already exists — skipping."
else
  cat >> "$CONFIG" <<'TOML'

[model_providers.smooth-llm-proxy]
name = "Smooth LLM Imposter"
base_url = "http://127.0.0.1:5080/openai"
wire_api = "responses"
request_max_retries = 3
stream_max_retries = 10
stream_idle_timeout_ms = 300000
TOML
  echo "Added: [model_providers.smooth-llm-proxy]"
fi
