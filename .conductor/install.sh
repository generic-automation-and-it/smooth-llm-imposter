#!/usr/bin/env bash
set -euo pipefail

# Vendoring installer for the .conductor kit: downloads a release tarball,
# extracts it into .conductor/, stamps .conductor/.kit-version. Nothing is
# fetched at runtime — the lifecycle scripts must work offline.
#
# Run from the root of the target repo. No authentication needed (public repo).
#
#   curl -fsSL https://raw.githubusercontent.com/generic-automation-and-it/smooth-llm-imposter/main/.conductor/install.sh | bash
#   curl -fsSL .../releases/download/vX.Y.Z/install.sh | bash -s -- --ref vX.Y.Z
#   bash .conductor/install.sh --check
#
# v0.0.1 and v1.0.0 are permanently asset-less — do not pin to them.

# Piped (`curl … | bash`), BASH_SOURCE is unset: a bare reference aborts under
# `set -u`, and a dirname fallback would drop the kit in the repo root.
# Override with INSTALL_DIR=/path.
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

# --ref pins a version; otherwise resolve the latest release.
if [ -n "$REF" ]; then
  TAG="$REF"
else
  echo "Resolving latest release..." >&2
  # `|| true`: /releases/latest 404s when there is no stable release (it ignores
  # pre-releases), and a bare `curl -fsSL` aborts under `set -e` before the
  # message below can explain why.
  TAG=$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" 2>/dev/null |
    awk -F'"' '/"tag_name"/{print $4; exit}' || true)
  if [ -z "$TAG" ]; then
    NEWEST=$(curl -fsSL "https://api.github.com/repos/$REPO/releases?per_page=1" 2>/dev/null |
      awk -F'"' '/"tag_name"/{print $4; exit}' || true)
    echo "No stable release available." >&2
    if [ -n "$NEWEST" ]; then
      echo "Newest is $NEWEST, a pre-release — install it explicitly: --ref $NEWEST" >&2
    else
      echo "This repository has no releases yet." >&2
    fi
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

# Staged next to the kit so a failed download changes nothing.
TMP_DIR="$(mktemp -d "${KIT_DIR}/.install-tmp-XXXXXX")"
trap 'rm -rf "$TMP_DIR"' EXIT

curl -fsSL "$DOWNLOAD_URL" -o "$TMP_DIR/$TARBALL"

# macOS has no sha256sum, only `shasum -a 256`.
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
  # Exit status on JUST our tarball: grepping the checker's output would match
  # OK and FAILED alike, and other entries are files we never downloaded.
  ( cd "$TMP_DIR" && grep -F " $TARBALL" SHA256SUMS > only-ours.sums \
      && "${SHA_CMD[@]}" -c only-ours.sums >/dev/null ) || {
    echo "Checksum verification FAILED for $TARBALL — refusing to install." >&2
    exit 1
  }
  echo "Checksum OK." >&2
else
  echo "WARNING: SHA256SUMS not found for this release; skipping checksum verification." >&2
fi

# Tarball is rooted at `.conductor/`, so strip that prefix. Staged then copied,
# so a tar failure cannot leave a half-updated kit of two versions.
echo "Extracting into $KIT_DIR" >&2
STAGE_DIR="$TMP_DIR/stage"
mkdir -p "$STAGE_DIR"
tar -xzf "$TMP_DIR/$TARBALL" -C "$STAGE_DIR" --strip-components=1
# .kit-version is the canonical "installed by this script" marker; an
# unrelated .conductor/ directory is left to a blind overwrite.
if [ -f "$KIT_VERSION_FILE" ]; then
  if [ -t 0 ]; then
    existing=$(cat "$KIT_VERSION_FILE" 2>/dev/null || echo unknown)
    printf 'Existing kit (%s) found in %s. Overwrite (local edits will be lost)? [y/N] ' \
      "$existing" "$KIT_DIR" >&2
    read -r ans
    case "$ans" in y|Y|yes|YES) ;; *) echo "Aborted." >&2; exit 1 ;; esac
  fi
  cp -R "$KIT_DIR" "${KIT_DIR}.bak.$(date +%s)"
fi
mkdir -p "$KIT_DIR"
# Replace by unlink+rename, never `cp` in place. An upgrade overwrites this very
# script while bash is still reading it; truncating the inode makes the running
# shell execute garbage ("line 147: e: command not found"). Unlinking leaves the
# open inode alive until the process exits.
find "$STAGE_DIR" -mindepth 1 -maxdepth 1 | while IFS= read -r item; do
  name="$(basename "$item")"
  rm -rf "${KIT_DIR:?}/$name"
  mv "$item" "$KIT_DIR/$name"
done

# Stamp the installed version.
echo "$TAG" > "$KIT_VERSION_FILE"

echo "Kit $TAG installed into $KIT_DIR"
