#!/usr/bin/env bash
set -euo pipefail

# On-demand trigger: recreate the SmoothLlmImposter container without
# rerunning code-review-graph setup or recreating the workspace. Use after
# pulling a new image tag, rotating OPENCODE_API_KEY/OPENROUTER_API_KEY, or
# recovering from a crash-looped container.
#
# No CONDUCTOR_IS_LOCAL guard here or in imposter-container.sh, by design: this
# is a manual trigger, so pressing it must always do the work and report what
# happened, on a Mac as much as in a cloud sandbox. Only setup.sh short-circuits
# on a local workspace.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec "$SCRIPT_DIR/imposter-container.sh"
