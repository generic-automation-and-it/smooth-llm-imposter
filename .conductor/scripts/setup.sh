#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PORT="${PORT:-5080}"
CODEX_CONFIG="$HOME/.codex/config.toml"

# --- Configure Codex ---------------------------------------------------------
# Replace only Codex's selected provider and SmoothLlmImposter's own table.
# Preserve every unrelated setting, including MCP servers and RTK config.
echo "--- Configuring Codex ---"
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

# --- code-review-graph -------------------------------------------------------
# code-review-graph is installed in the snapshot, but both `install` and `build`
# are repository-scoped: `install` writes a repo-pinned `cwd` into each MCP
# config, and `build` writes the graph into the working tree. The clone only
# exists in the workspace, so run both here rather than during snapshot
# construction.
#
# --no-instructions is mandatory on every platform. Without it `install` appends
# a ~39-line MCP-tools section to CLAUDE.md, which in this repository is a
# committed symlink to AGENTS.md — the append lands in the root context file.
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

exec "$SCRIPT_DIR/imposter-container.sh"
