#!/usr/bin/env bash
set -euo pipefail

# Follows the container logs. A run script because `docker logs -f` never exits.
# Checks the daemon first: when dockerd is what died, `docker logs` reports that
# instead of the container output you came for.
CONTAINER_NAME="smooth-llm-imposter"
TAIL="${TAIL:-200}"

if [ "$(uname -s)" = "Linux" ]; then
  export DOCKER_HOST="${DOCKER_HOST:-unix:///var/run/docker.sock}"
fi

if docker info >/dev/null 2>&1; then
  DOCKER=(docker)
elif sudo -n docker info >/dev/null 2>&1; then
  DOCKER=(sudo -n docker)
else
  echo "Docker daemon is unreachable — there are no container logs to show." >&2
  echo "Run the restart-imposter trigger; it prints a full diagnostic instead." >&2
  exit 1
fi

if ! "${DOCKER[@]}" inspect "$CONTAINER_NAME" >/dev/null 2>&1; then
  echo "No container named $CONTAINER_NAME exists. Run the restart-imposter trigger first." >&2
  exit 1
fi

echo "--- following $CONTAINER_NAME (last $TAIL lines, Ctrl-C to stop) ---"
exec "${DOCKER[@]}" logs -f --tail "$TAIL" "$CONTAINER_NAME"
