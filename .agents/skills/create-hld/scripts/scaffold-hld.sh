#!/usr/bin/env bash
# Skill: create-hld
# Deterministic scaffolder for a design-only High-Level Design (HLD) folder.
#
# Computes the next NNN index under .docs/hlds/, creates the folder skeleton
# (README.md, AGENTS.md, diagrams/c4-context.md, ladrs/, nfrs/, optional
# examples/) by copying the skill's asset templates with placeholder
# substitution, then prints a JSON object of the created paths.
#
# Tool agnostic: bash + coreutils only. Repo root discovered via git.
# The skill carries the *judgment* (what intent/decisions/NFRs to write);
# this script only lays down the deterministic skeleton.

set -euo pipefail

SCRIPT_DIR=$(cd -P "$(dirname "$0")" && pwd -P)
SKILL_DIR=$(cd -P "$SCRIPT_DIR/.." && pwd -P)
ASSETS_DIR="$SKILL_DIR/assets"

SLUG=""
TITLE=""
WITH_EXAMPLES=0

usage() {
    cat <<'USAGE'
Usage:
  scaffold-hld.sh <kebab-case-slug> [--title "Human Title"] [--examples]

Arguments:
  <kebab-case-slug>   Lowercase, hyphen-separated, e.g. payments-platform
                      Produces .docs/hlds/NNN-<slug>/

Options:
  --title "..."       Human-readable initiative title for the README H1.
                      Defaults to the slug, title-cased.
  --examples          Also create an empty examples/ folder (code samples only).

Output:
  JSON object on stdout listing the index, folder, and every created path.
USAGE
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        -h|--help) usage; exit 0 ;;
        --title) TITLE="${2:-}"; shift 2 ;;
        --examples) WITH_EXAMPLES=1; shift ;;
        --) shift; break ;;
        -*) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
        *)
            if [ -z "$SLUG" ]; then SLUG="$1"; shift
            else echo "Unexpected argument: $1" >&2; usage >&2; exit 2; fi
            ;;
    esac
done

[ -n "$SLUG" ] || { echo "Error: slug is required." >&2; usage >&2; exit 2; }

# Validate kebab-case: lowercase letters, digits, single hyphens; no leading/trailing/double hyphen.
if ! printf '%s' "$SLUG" | grep -Eq '^[a-z0-9]+(-[a-z0-9]+)*$'; then
    echo "Error: slug must be kebab-case (lowercase letters, digits, single hyphens): '$SLUG'" >&2
    exit 2
fi

REPO_ROOT=$(git rev-parse --show-toplevel 2>/dev/null || pwd)
HLD_ROOT="$REPO_ROOT/.docs/hlds"
mkdir -p "$HLD_ROOT"

# Compute next 3-digit index (max existing NNN + 1, floor 001).
max=0
for d in "$HLD_ROOT"/[0-9][0-9][0-9]-*/; do
    [ -d "$d" ] || continue
    base=$(basename "$d")
    n=${base%%-*}
    n=$((10#$n))
    [ "$n" -gt "$max" ] && max=$n
done
INDEX=$(printf '%03d' "$((max + 1))")

TARGET="$HLD_ROOT/${INDEX}-${SLUG}"
if [ -e "$TARGET" ]; then
    echo "Error: target already exists: $TARGET" >&2
    exit 1
fi

# Derive title from slug if not supplied: hyphens -> spaces, title-case each word.
if [ -z "$TITLE" ]; then
    TITLE=$(printf '%s' "$SLUG" | tr '-' ' ' | awk '{ for (i=1;i<=NF;i++) $i=toupper(substr($i,1,1)) substr($i,2) } 1')
fi
SLUG_UPPER=$(printf '%s' "$SLUG" | tr '[:lower:]-' '[:upper:]_')
DATE=$(date +%F)

# Render a template file to a destination with placeholder substitution.
# Placeholders: {{INDEX}} {{SLUG}} {{SLUG_UPPER}} {{TITLE}} {{DATE}}
render() {
    local src="$1" dst="$2"
    [ -f "$src" ] || { echo "Error: missing template: $src" >&2; exit 1; }
    sed -e "s|{{INDEX}}|${INDEX}|g" \
        -e "s|{{SLUG}}|${SLUG}|g" \
        -e "s|{{SLUG_UPPER}}|${SLUG_UPPER}|g" \
        -e "s|{{TITLE}}|${TITLE}|g" \
        -e "s|{{DATE}}|${DATE}|g" \
        "$src" > "$dst"
}

mkdir -p "$TARGET/diagrams" "$TARGET/ladrs" "$TARGET/nfrs"

created=()
render "$ASSETS_DIR/README.template.md"     "$TARGET/README.md";              created+=("$TARGET/README.md")
render "$ASSETS_DIR/AGENTS.template.md"      "$TARGET/AGENTS.md";              created+=("$TARGET/AGENTS.md")
render "$ASSETS_DIR/c4-context.template.md"  "$TARGET/diagrams/c4-context.md"; created+=("$TARGET/diagrams/c4-context.md")
render "$ASSETS_DIR/LADR.template.md"        "$TARGET/ladrs/LADR-01-example.md"; created+=("$TARGET/ladrs/LADR-01-example.md")
render "$ASSETS_DIR/NFR.template.md"         "$TARGET/nfrs/NFR-01-example.md";   created+=("$TARGET/nfrs/NFR-01-example.md")

if [ "$WITH_EXAMPLES" -eq 1 ]; then
    mkdir -p "$TARGET/examples"
    render "$ASSETS_DIR/examples.README.template.md" "$TARGET/examples/README.md"
    created+=("$TARGET/examples/README.md")
fi

# Emit JSON (paths relative to repo root for portability).
rel() { printf '%s' "${1#"$REPO_ROOT"/}"; }
printf '{\n'
printf '  "index": "%s",\n' "$INDEX"
printf '  "slug": "%s",\n' "$SLUG"
printf '  "title": "%s",\n' "$TITLE"
printf '  "folder": "%s",\n' "$(rel "$TARGET")"
printf '  "created": [\n'
for i in "${!created[@]}"; do
    sep=","; [ "$i" -eq "$((${#created[@]} - 1))" ] && sep=""
    printf '    "%s"%s\n' "$(rel "${created[$i]}")" "$sep"
done
printf '  ]\n'
printf '}\n'
