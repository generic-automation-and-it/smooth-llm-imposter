#!/usr/bin/env bash
# Local short-circuit, then Codex config, then code-review-graph, then the
# container. The container stays LAST: hoisting it was tried and reverted —
# on a restarted VM the daemon vanished mid-health-check. Do not reorder.
set -euo pipefail

# Local workspaces already have Docker Desktop running the container; setup
# would only clobber it, and auto_run_after_setup drives the container instead.
# Defaulted so a hand-run in a plain shell does not abort under `set -u`.
if [ "${CONDUCTOR_IS_LOCAL:-0}" = "1" ]; then
  exit 0
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PORT="${PORT:-5080}"
CODEX_CONFIG="$HOME/.codex/config.toml"

# --- Configure Codex ---------------------------------------------------------
# Replaces only the selected provider and our own table; everything else stays.
if ! command -v python3 >/dev/null 2>&1; then
  echo "python3 not found; skipping Codex configuration." >&2
else
  echo "--- Configuring Codex ---"
  mkdir -p "$(dirname "$CODEX_CONFIG")"
  touch "$CODEX_CONFIG"
  [ -f "$CODEX_CONFIG.bak" ] || cp -p "$CODEX_CONFIG" "$CODEX_CONFIG.bak"

  python3 - "$CODEX_CONFIG" "$PORT" <<'PY'
from pathlib import Path
import os
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
base_url = "http://127.0.0.1:{port}/openai/v1"
wire_api = "responses"
requires_openai_auth = true
request_max_retries = 3
stream_max_retries = 10
stream_idle_timeout_ms = 300000
"""

table_pattern = re.compile(
    r"(?ms)^\[model_providers\.smooth-llm-proxy\]\s*\n"
    r"(?:(?!^\[[^\]]+\]).)*"
    r"(?=\Z|^\[)"
)
if table_pattern.search(text):
    text = table_pattern.sub(smooth_table + "\n", text, count=1)
else:
    text = text.rstrip() + "\n\n" + smooth_table

# Atomic: a kill mid-write must not leave a half-written config.toml.
tmp_path = config_path.with_suffix(config_path.suffix + ".tmp")
tmp_path.write_text(text)
os.replace(tmp_path, config_path)
PY
fi

# Catches a silent regex miss before it becomes a runtime "provider not found".
if ! grep -Fqx 'model_provider = "smooth-llm-proxy"' "$CODEX_CONFIG"; then
  echo "Codex provider line not found in $CODEX_CONFIG after write — aborting before container start." >&2
  exit 1
fi
if ! grep -Fqx '[model_providers.smooth-llm-proxy]' "$CODEX_CONFIG"; then
  echo "Codex provider table not found in $CODEX_CONFIG after write — aborting before container start." >&2
  exit 1
fi

# --- code-review-graph -------------------------------------------------------
# install/build are repo-scoped, so they run here rather than in the snapshot.
# --no-instructions is mandatory everywhere: without it `install` appends to
# CLAUDE.md, a committed symlink to AGENTS.md. claude-code needs --no-skills
# --no-hooks too, because .claude is a symlink into tracked .agents/.
if command -v code-review-graph >/dev/null 2>&1 && git -C . rev-parse --git-dir >/dev/null 2>&1; then
  # Untracked and repo-local, so they never show up in a workspace diff.
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

exec "$SCRIPT_DIR/imposter-container.sh"
