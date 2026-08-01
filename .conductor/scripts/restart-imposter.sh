#!/usr/bin/env bash
set -euo pipefail

# restart-imposter recreates the imposter container without rerunning
# the Codex or `code-review-graph` setup steps (only `setup.sh` runs
# those). Use this for a new image tag, a rotated API key, or a
# crash-looped container.
#
# No CONDUCTOR_IS_LOCAL guard here or in imposter-container.sh, by design: this
# is a manual trigger, so pressing it must always do the work and report what
# happened, on a Mac as much as in a cloud sandbox. Only setup.sh short-circuits
# on a local workspace.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec "$SCRIPT_DIR/imposter-container.sh"
