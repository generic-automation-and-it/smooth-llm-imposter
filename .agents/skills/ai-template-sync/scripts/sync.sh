#!/usr/bin/env bash
# ai-template-sync — deterministic file-sync for the smooth-devex-template scaffold.
#
# Executes the mechanical copy/symlink steps (Sections A–E of SKILL.md). All
# DECISIONS (which tools, overwrite scope, dotnet opt-in, per-file selection)
# stay with the agent; this script only carries out decisions passed as flags.
#
# Usage:
#   sync.sh --landing <dir> --tools claude,codex,copilot [--template <dir>] \
#           [--dotnet] [--overwrite global|none] [--rules-only]
#
#   --template     template repo root (default: current git toplevel)
#   --template-url git URL of the template; shallow-cloned to a temp dir and
#                  removed on exit. Mutually exclusive with --template. Use for
#                  remote / web runs where the template is not checked out locally.
#   --template-ref tag/branch/SHA to pin the --template-url clone to (reproducible
#                  rule installs). Requires --template-url.
#   --landing      landing repo root (required, must exist)
#   --tools      comma-separated subset of: claude,codex,copilot
#   --dotnet     also copy .NET solutioning (Section E); never overwrites
#   --overwrite  global = clobber existing agentic files
#                none   = additive only, never clobber existing files (default)
#   --rules-only sync ONLY the rule system: .github/instructions/ tree + the
#                .agents/rules symlink. Skips Sections A/B/C/E and
#                copilot-instructions.md; ignores --tools/--dotnet. Landing-only
#                rule files are never deleted (UPSERT, even in global mode).
#
# Overwrite note: this script supports GLOBAL (overwrite everything) and NONE
# (additive). True per-file SELECTIVE overwrite is the agent's job — copy the
# user-approved files first, then run this with --overwrite none for the rest.
set -euo pipefail

TEMPLATE=""
TEMPLATE_URL=""
TEMPLATE_REF=""
LANDING=""
TOOLS=""
DOTNET=0
OVERWRITE="none"
RULES_ONLY=0

while [ $# -gt 0 ]; do
  case "$1" in
    --template)     TEMPLATE="$2";     shift 2 ;;
    --template-url) TEMPLATE_URL="$2"; shift 2 ;;
    --template-ref) TEMPLATE_REF="$2"; shift 2 ;;
    --landing)      LANDING="$2";      shift 2 ;;
    --tools)        TOOLS="$2";        shift 2 ;;
    --dotnet)       DOTNET=1;          shift ;;
    --overwrite)    OVERWRITE="$2";    shift 2 ;;
    --rules-only)   RULES_ONLY=1;      shift ;;
    *) echo "unknown arg: $1" >&2; exit 64 ;;
  esac
done

[ -n "$LANDING" ] || { echo "--landing is required" >&2; exit 64; }
[ -d "$LANDING" ] || { echo "landing dir does not exist: $LANDING" >&2; exit 66; }
case "$OVERWRITE" in global|none) ;; *) echo "--overwrite must be global|none" >&2; exit 64 ;; esac
[ -z "$TEMPLATE_REF" ] || [ -n "$TEMPLATE_URL" ] || { echo "--template-ref requires --template-url" >&2; exit 64; }

# Resolve the template source: --template-url (clone) XOR --template XOR default (git toplevel).
if [ -n "$TEMPLATE_URL" ]; then
  [ -z "$TEMPLATE" ] || { echo "--template and --template-url are mutually exclusive" >&2; exit 64; }
  CLONE_DIR="$(mktemp -d)"
  trap 'rm -rf "$CLONE_DIR"' EXIT
  echo "==> cloning template: $TEMPLATE_URL${TEMPLATE_REF:+ @ $TEMPLATE_REF}"
  if [ -n "$TEMPLATE_REF" ]; then
    # --branch covers tags and branches; fall back to full clone + checkout for SHAs
    if ! git clone --depth 1 --branch "$TEMPLATE_REF" "$TEMPLATE_URL" "$CLONE_DIR" 2>/dev/null; then
      rm -rf "$CLONE_DIR"; mkdir -p "$CLONE_DIR"
      git clone "$TEMPLATE_URL" "$CLONE_DIR"
      git -C "$CLONE_DIR" checkout --detach "$TEMPLATE_REF"
    fi
  else
    git clone --depth 1 "$TEMPLATE_URL" "$CLONE_DIR"
  fi
  TEMPLATE="$CLONE_DIR"
elif [ -z "$TEMPLATE" ]; then
  TEMPLATE="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
fi
[ -d "$TEMPLATE/.agents" ] || { echo "template does not look like the scaffold (no .agents/): $TEMPLATE" >&2; exit 66; }

has_tool() { case ",$TOOLS," in *",$1,"*) return 0 ;; *) return 1 ;; esac; }

# Rules-only mode — distribute/update the rule system and nothing else.
if [ "$RULES_ONLY" -eq 1 ]; then
  [ -d "$TEMPLATE/.github/instructions" ] || { echo "template has no .github/instructions/: $TEMPLATE" >&2; exit 66; }
  echo "==> rules-only: syncing .github/instructions/ + .agents/rules symlink"
  cd "$LANDING"
  git config core.symlinks true 2>/dev/null || true
  mkdir -p .github
  if [ "$OVERWRITE" = "global" ]; then
    # overwrite same-named files; landing-only rules are kept (UPSERT, no --delete)
    rsync -a "$TEMPLATE/.github/instructions/" .github/instructions/
  else
    rsync -a --ignore-existing "$TEMPLATE/.github/instructions/" .github/instructions/
  fi
  if [ -e .agents/rules ] && [ ! -L .agents/rules ]; then
    echo "    refusing: .agents/rules is a real directory, not a symlink — move its files into .github/instructions/ first" >&2
    exit 65
  fi
  if [ -d .agents ]; then
    rm -f .agents/rules
    ln -sf ../.github/instructions .agents/rules
  else
    echo "    note: no .agents/ dir in landing repo — skipped the .agents/rules symlink"
  fi
  echo "==> sync complete (rules only)"
  exit 0
fi

# Section A — .agents base tree
echo "==> Section A: syncing .agents/ tree"
if [ "$OVERWRITE" = "global" ]; then
  rsync -a "$TEMPLATE/.agents/" "$LANDING/.agents/"
else
  rsync -a --ignore-existing "$TEMPLATE/.agents/" "$LANDING/.agents/"
fi

cd "$LANDING"
git config core.symlinks true 2>/dev/null || true

# Section B — Claude Code (also lays the Cursor symlink — same scaffold)
if has_tool claude; then
  echo "==> Section B: Claude Code symlinks"
  ln -sf .agents .claude
  ln -sf .agents .cursor
  ln -sf AGENTS.md CLAUDE.md
  ln -sf AGENTS.md GEMINI.md
fi

# Section C — Codex
if has_tool codex; then
  echo "==> Section C: Codex symlink"
  ln -sf .agents .codex
fi

# Section D — GitHub Copilot
if has_tool copilot; then
  echo "==> Section D: Copilot rules dir + instructions"
  mkdir -p .github
  if [ "$OVERWRITE" = "global" ] || [ ! -d .github/instructions ]; then
    rm -rf .github/instructions
    cp -R "$TEMPLATE/.github/instructions" .github/instructions
  fi
  # .agents/rules must be a symlink back to the real instructions dir. Refuse to
  # clobber a real directory (a landing repo's own rule set) — only replace a symlink.
  if [ -e .agents/rules ] && [ ! -L .agents/rules ]; then
    echo "    refusing: .agents/rules is a real directory, not a symlink — leaving it untouched" >&2
    exit 65
  fi
  rm -f .agents/rules
  ln -sf ../.github/instructions .agents/rules
  if [ "$OVERWRITE" = "global" ] || [ ! -f .github/copilot-instructions.md ]; then
    cp "$TEMPLATE/.github/copilot-instructions.md" .github/copilot-instructions.md
  fi
fi

# Section E — .NET solutioning (never overwrites)
if [ "$DOTNET" -eq 1 ]; then
  echo "==> Section E: .NET solutioning"
  if ls "$LANDING"/*.slnx "$LANDING"/*.sln >/dev/null 2>&1; then
    echo "    skipped: landing repo already has a solution file"
  else
    for item in Directory.Build.props Directory.Packages.props NuGet.Config src tests; do
      [ -e "$TEMPLATE/$item" ] || continue
      if [ -e "$LANDING/$item" ]; then
        echo "    skipped (exists): $item"
        continue
      fi
      cp -R "$TEMPLATE/$item" "$LANDING/$item"
    done
    for slnx in "$TEMPLATE"/*.slnx; do
      [ -e "$slnx" ] || continue
      dest="$LANDING/$(basename "$slnx")"
      if [ -e "$dest" ]; then
        echo "    skipped (exists): $(basename "$slnx")"
        continue
      fi
      cp "$slnx" "$dest"
    done
    echo "    NOTE: rename Project.* -> <ActualProjectName> in names/namespaces afterwards"
  fi
fi

echo "==> sync complete"
