#!/usr/bin/env bash
set -euo pipefail

# Recreates the container only — no Codex or code-review-graph steps.
# No CONDUCTOR_IS_LOCAL guard, deliberately: a manual trigger must always act
# and report, on a Mac too. See .conductor/AGENTS.md.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec "$SCRIPT_DIR/imposter-container.sh"
