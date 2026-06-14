#!/usr/bin/env bash
# Skill: load-agents-context
# PostToolUse hook — injects *AGENTS.md context files from ancestor directories
# on the first Read/Edit of a file in that directory tree per session.
#
# Tool agnostic: uses agent-specific session IDs or PPID fallback.
# Transferable: no hardcoded paths; repo root discovered via git.

set -euo pipefail

TOOL_NAME=""
FILE_PATH=""
SESSION_ID_OVERRIDE=""
RESET_SESSION=0

usage() {
    cat <<'USAGE'
Usage:
  load-agents-context.sh [--tool TOOL] [--session-id ID] [--reset-session] --file PATH
  load-agents-context.sh [--tool TOOL] [--session-id ID] [--reset-session] PATH
  printf '%s' '{"tool_name":"Read","tool_input":{"file_path":"PATH"}}' | load-agents-context.sh

Examples:
  CODEX_SESSION_ID="$CODEX_THREAD_ID" load-agents-context.sh --tool Codex --file src/Foo.cs
  COPILOT_SESSION_ID="copilot-vscode-session" load-agents-context.sh --tool Copilot --file src/Foo.cs
USAGE
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --file)
            [ "$#" -lt 2 ] && exit 0
            FILE_PATH="$2"
            shift 2
            ;;
        --tool)
            [ "$#" -lt 2 ] && exit 0
            TOOL_NAME="$2"
            shift 2
            ;;
        --session-id)
            [ "$#" -lt 2 ] && exit 0
            SESSION_ID_OVERRIDE="$2"
            shift 2
            ;;
        --reset-session)
            RESET_SESSION=1
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        --)
            shift
            break
            ;;
        -*)
            exit 0
            ;;
        *)
            FILE_PATH="${FILE_PATH:-$1}"
            shift
            ;;
    esac
done

if [ -z "$FILE_PATH" ] && [ ! -t 0 ]; then
    PAYLOAD=$(cat)
    TOOL_NAME=$(printf '%s' "$PAYLOAD" | jq -r '.tool_name // empty' 2>/dev/null || true)
    FILE_PATH=$(printf '%s' "$PAYLOAD" | jq -r '.tool_input.file_path // .tool_input.path // empty' 2>/dev/null || true)
fi

# Only act on actual file read/edit/write operations — not search, glob, or bash
if [ -n "$TOOL_NAME" ]; then
    case "$TOOL_NAME" in
        Read|Edit|Write|Codex|Copilot|codex|copilot) ;;
        *) exit 0 ;;
    esac
fi

[ -z "$FILE_PATH" ] && exit 0

# --- Resolve absolute path (physical path for symlink compatibility) ---
if [ -f "$FILE_PATH" ]; then
    ABS_PATH=$(cd -P "$(dirname "$FILE_PATH")" 2>/dev/null && pwd -P)/$(basename "$FILE_PATH")
elif [ -d "$(dirname "$FILE_PATH")" ]; then
    ABS_PATH=$(cd -P "$(dirname "$FILE_PATH")" 2>/dev/null && pwd -P)/$(basename "$FILE_PATH")
else
    exit 0
fi

FILE_DIR=$(dirname "$ABS_PATH")

# --- Session-scoped deduplication tracker ---
if [ -n "$SESSION_ID_OVERRIDE" ]; then
    SESSION_ID="$SESSION_ID_OVERRIDE"
else
    case "$TOOL_NAME" in
        Codex|codex)
            SESSION_ID="${CODEX_SESSION_ID:-${CODEX_THREAD_ID:-${PPID:-$$}}}"
            ;;
        Copilot|copilot)
            SESSION_ID="${COPILOT_SESSION_ID:-${GITHUB_COPILOT_SESSION_ID:-${PPID:-$$}}}"
            ;;
        Read|Edit)
            SESSION_ID="${CLAUDE_SESSION_ID:-${PPID:-$$}}"
            ;;
        *)
            SESSION_ID="${CLAUDE_SESSION_ID:-${CODEX_SESSION_ID:-${CODEX_THREAD_ID:-${COPILOT_SESSION_ID:-${GITHUB_COPILOT_SESSION_ID:-${PPID:-$$}}}}}}"
            ;;
    esac
fi
SAFE_SESSION_ID=$(printf '%s' "$SESSION_ID" | tr -c 'A-Za-z0-9._-' '_')
TRACKER="/tmp/.agents_ctx_${SAFE_SESSION_ID}"
[ "$RESET_SESSION" -eq 1 ] && rm -f "$TRACKER" 2>/dev/null || true
touch "$TRACKER" 2>/dev/null || exit 0

# --- Find git repo root to stop walk at repo boundary ---
REPO_ROOT=$(git -C "$FILE_DIR" rev-parse --show-toplevel 2>/dev/null || true)

# --- Skip directories that are already auto-loaded by the AI tool ---
is_auto_loaded_dir() {
    case "$1" in
        */.agents/rules/*|*/.ai/rules/*|*/.claude/rules/*|*/.cursor/rules/*|*/.github/instructions/*) return 0 ;;
    esac
    return 1
}

# --- Walk ancestor directories, collect new AGENTS.md files ---
DIR="$FILE_DIR"
DEPTH=0
TO_LOAD=()

while [ "$DEPTH" -lt 20 ]; do
    # Non-recursive scan of this directory only (maxdepth 1)
    while IFS= read -r agents_file; do
        [ -z "$agents_file" ] && continue
        is_auto_loaded_dir "$agents_file" && continue
        if ! grep -qxF "$agents_file" "$TRACKER" 2>/dev/null; then
            TO_LOAD+=("$agents_file")
            printf '%s\n' "$agents_file" >> "$TRACKER" || true
        fi
    done < <(find "$DIR" -maxdepth 1 \( -name "AGENTS.md" -o -name "*_AGENTS.md" \) -type f 2>/dev/null | sort)

    # Stop at repo root
    [ -n "$REPO_ROOT" ] && [ "$DIR" = "$REPO_ROOT" ] && break

    NEXT=$(dirname "$DIR")
    [ "$NEXT" = "$DIR" ] && break  # Filesystem root guard
    DIR="$NEXT"
    DEPTH=$((DEPTH + 1))
done

# --- Skill-on-path injection ---
# When the touched file matches a known pattern, inject the related skill/rule once per session.
inject_if_new() {
    local target="$1"
    [ -f "$target" ] || return 0
    if ! grep -qxF "$target" "$TRACKER" 2>/dev/null; then
        TO_LOAD+=("$target")
        printf '%s\n' "$target" >> "$TRACKER" || true
    fi
}

if [ -n "$REPO_ROOT" ]; then
    case "$ABS_PATH" in
        */.agents/rules/*|*/.claude/rules/*|*/.cursor/rules/*|*/.github/instructions/*)
            inject_if_new "${REPO_ROOT}/.agents/skills/manage-rule-system/SKILL.md"
            ;;
    esac
    case "$(basename "$ABS_PATH")" in
        AGENTS.md|CLAUDE.md|GEMINI.md|*_AGENTS.md)
            inject_if_new "${REPO_ROOT}/.agents/rules/meta/knowledge-conventional-contexts-quality.instructions.md"
            ;;
    esac
fi

# --- Emit context block (stdout is injected into the conversation) ---
if [ "${#TO_LOAD[@]}" -gt 0 ]; then
    ROOT="${REPO_ROOT:-$FILE_DIR}"
    printf '<context-auto-loaded>\n'
    for f in "${TO_LOAD[@]}"; do
        rel="${f#"$ROOT"/}"
        printf '\n## Context: %s\n' "$rel"
        cat "$f" 2>/dev/null || true
    done
    printf '</context-auto-loaded>\n'
fi

exit 0
