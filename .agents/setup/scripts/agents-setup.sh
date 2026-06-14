#!/bin/bash
# AI Agent Tools symlink setup for Mac/Linux
# Supports: Claude Code, GitHub Copilot, Cursor, OpenAI Codex

# Get the repo root (parent of .agents)
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../" && pwd)"
cd "$REPO_ROOT"

# Helper function to create symlink
create_symlink() {
    local link_path="$1"
    local target_path="$2"

    # Remove existing symlink if it exists
    if [ -L "$link_path" ] || [ -e "$link_path" ]; then
        rm -f "$link_path"
    fi

    # Create symlink
    if ln -s "$target_path" "$link_path" 2>/dev/null; then
        echo "✓ $link_path → $target_path"
        return 0
    else
        echo "✗ Failed to create symlink: $link_path → $target_path" >&2
        return 1
    fi
}

all_success=true

# Create directory symlinks for tool-specific rule paths
echo "Creating directory symlinks for AI agent tools..."
create_symlink ".claude" ".agents" || all_success=false
create_symlink ".codex" ".agents" || all_success=false
create_symlink ".cursor" ".agents" || all_success=false

# Create file symlinks for context files
echo ""
echo "Creating file symlinks for context files..."
create_symlink "CLAUDE.md" "AGENTS.md" || all_success=false
create_symlink "GEMINI.md" "AGENTS.md" || all_success=false

# Configure git to handle symlinks properly
echo ""
echo "Configuring git for symlink support..."
git config core.symlinks true
echo "✓ Git configured for symlinks"

if [ "$all_success" = true ]; then
    echo ""
    echo "✓ All symlinks created successfully"
    exit 0
else
    echo ""
    echo "✗ One or more symlinks failed. Check permissions and ensure .agents exists." >&2
    exit 1
fi
