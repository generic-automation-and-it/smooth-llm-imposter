#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

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
