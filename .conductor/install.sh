#!/usr/bin/env bash
set -euo pipefail

# Vendoring installer for the .conductor kit.
# Downloads a release tarball from GitHub, extracts it into .conductor/,
# and stamps the installed version in .conductor/.kit-version.
#
# The lifecycle scripts (setup.sh, restart-imposter.sh, imposter-logs.sh)
# reference files under .conductor/scripts/ by relative path — they never
# fetch anything at runtime. The kit is a one-time install, not a runtime
# dependency. This avoids reintroducing the class of failure that the
# --pull=always bug exposed: a network blip during setup destroying the
# working container.
#
# Usage:
#   curl -fsSL https://github.com/generic-automation-and-it/smooth-llm-imposter/releases/download/<tag>/install.sh | bash
#   curl -fsSL https://github.com/generic-automation-and-it/smooth-llm-imposter/releases/download/<tag>/install.sh | bash -s -- --ref <tag>
#   bash .conductor/install.sh --check

# Resolve where the kit belongs. Two invocation styles, and they behave very
# differently:
#
#   bash .conductor/install.sh    → BASH_SOURCE[0] is set; the kit is the
#                                   directory this file already lives in.
#   curl … | bash                 → BASH_SOURCE[0] is UNSET. Under `set -u` a
#                                   bare reference aborts, and the naive
#                                   `dirname` fallback yields "." — which would
#                                   drop the kit in the repo root instead of
#                                   .conductor/. Target $PWD/.conductor instead.
#
# Override with INSTALL_DIR=/path when installing somewhere non-standard.
if [ -n "${BASH_SOURCE[0]:-}" ]; then
  KIT_DIR="${INSTALL_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)}"
else
  KIT_DIR="${INSTALL_DIR:-$PWD/.conductor}"
fi
mkdir -p "$KIT_DIR"
KIT_DIR="$(cd "$KIT_DIR" && pwd)"
KIT_VERSION_FILE="$KIT_DIR/.kit-version"

REPO="generic-automation-and-it/smooth-llm-imposter"
BASE_URL="https://github.com/$REPO/releases/download"

REF=""
CHECK_ONLY=0

while [ $# -gt 0 ]; do
  case "$1" in
    --ref)
      shift
      REF="${1:?--ref requires a version tag}"
      ;;
    --check)
      CHECK_ONLY=1
      ;;
    *)
      echo "Unknown option: $1" >&2
      echo "Usage: install.sh [--ref <tag>] [--check]" >&2
      exit 1
      ;;
  esac
  shift
done

if [ "$CHECK_ONLY" = "1" ]; then
  if [ -f "$KIT_VERSION_FILE" ]; then
    cat "$KIT_VERSION_FILE"
  else
    echo "No kit version file found at $KIT_VERSION_FILE" >&2
    exit 1
  fi
  exit 0
fi

# Resolve the tag to download. --ref pins an explicit version;
# otherwise we fetch the latest release tag via the GitHub API.
if [ -n "$REF" ]; then
  TAG="$REF"
else
  echo "Resolving latest release tag..." >&2
  TAG=$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" |
    awk -F'"' '/"tag_name"/{print $4; exit}')
  if [ -z "$TAG" ]; then
    echo "Failed to resolve the latest release tag." >&2
    exit 1
  fi
  echo "Latest release: $TAG" >&2
fi

case "$TAG" in
  v*) ;;
  *) TAG="v$TAG" ;;
esac
TARBALL="conductor-kit-${TAG#v}.tar.gz"
DOWNLOAD_URL="$BASE_URL/${TAG}/$TARBALL"

echo "Downloading $TARBALL from $DOWNLOAD_URL" >&2

# Download to a temporary directory next to .conductor so the extract
# is atomic: either the whole kit lands, or nothing changes.
TMP_DIR="$(mktemp -d "${KIT_DIR}/.install-tmp-XXXXXX")"
trap 'rm -rf "$TMP_DIR"' EXIT

curl -fsSL "$DOWNLOAD_URL" -o "$TMP_DIR/$TARBALL"

# Verify the checksum if SHA256SUMS is available. macOS has no sha256sum — it
# ships `shasum -a 256` — and this installer is expected to run on developer
# Macs as well as Linux sandboxes.
if command -v sha256sum >/dev/null 2>&1; then
  SHA_CMD=(sha256sum)
elif command -v shasum >/dev/null 2>&1; then
  SHA_CMD=(shasum -a 256)
else
  SHA_CMD=()
fi

SHA_URL="$BASE_URL/${TAG}/SHA256SUMS"
if [ ${#SHA_CMD[@]} -eq 0 ]; then
  echo "WARNING: no sha256sum/shasum available; skipping checksum verification." >&2
elif curl -fsSL "$SHA_URL" -o "$TMP_DIR/SHA256SUMS" 2>/dev/null; then
  echo "Verifying checksum..." >&2
  # Check the exit status of the checker on JUST our tarball. The previous
  # version piped into `grep -q "$TARBALL"`, which matches the filename whether
  # the line says OK or FAILED, and made a multi-file SHA256SUMS fail because
  # entries for files we never downloaded are reported missing.
  ( cd "$TMP_DIR" && grep -F " $TARBALL" SHA256SUMS > only-ours.sums \
      && "${SHA_CMD[@]}" -c only-ours.sums >/dev/null ) || {
    echo "Checksum verification FAILED for $TARBALL — refusing to install." >&2
    exit 1
  }
  echo "Checksum OK." >&2
else
  echo "WARNING: SHA256SUMS not found for this release; skipping checksum verification." >&2
fi

# Extract. The tarball is rooted at `.conductor/`, so --strip-components=1
# removes that prefix and the contents land directly in $KIT_DIR.
#
# Staged, then swapped file-by-file: extracting straight into a live $KIT_DIR
# means a failure part-way leaves a half-updated kit — scripts from two
# versions calling each other. Staging does not make this a true atomic
# rename (the swap is per-file), but it does mean a download or tar failure
# changes nothing at all.
echo "Extracting into $KIT_DIR" >&2
STAGE_DIR="$TMP_DIR/stage"
mkdir -p "$STAGE_DIR"
tar -xzf "$TMP_DIR/$TARBALL" -C "$STAGE_DIR" --strip-components=1
if [ -d "$KIT_DIR" ]; then
  if [ -t 0 ]; then
    printf 'Existing .conductor found. Overwrite (local edits will be lost)? [y/N] ' >&2
    read -r ans
    case "$ans" in y|Y|yes|YES) ;; *) echo "Aborted." >&2; exit 1 ;; esac
  fi
  cp -R "$KIT_DIR" "${KIT_DIR}.bak.$(date +%s)"
fi
mkdir -p "$KIT_DIR"
cp -R "$STAGE_DIR/." "$KIT_DIR/"

# Stamp the installed version.
echo "$TAG" > "$KIT_VERSION_FILE"

echo "Kit $TAG installed into $KIT_DIR"
