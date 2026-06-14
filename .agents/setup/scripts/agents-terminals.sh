#!/bin/bash
# AI Agent Tools - Multi-Terminal Launcher for Mac/Linux
# Opens separate terminal tabs/windows for different AI tools and models

# Get the repo root
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../" && pwd)"
cd "$REPO_ROOT"

# Check if we're on macOS or Linux
if [[ "$OSTYPE" == "darwin"* ]]; then
    # macOS - use Terminal.app with AppleScript
    osascript <<EOF
        tell application "Terminal"
            activate

            -- Claude Opus
            do script "cd '$REPO_ROOT' && claude-yolo --model Opus"
            tell front window
                set custom title of tab 1 to "Claude (Opus)"
            end tell

            -- Claude Sonnet
            do script "cd '$REPO_ROOT' && claude-yolo --model Sonnet"
            tell front window
                set custom title of tab 2 to "Claude (Sonnet)"
            end tell

            -- Codex GPT-5.4
            do script "cd '$REPO_ROOT' && codex --model gpt-5.4 --yolo"
            tell front window
                set custom title of tab 3 to "Codex (GPT-5.4)"
            end tell

            -- Copilot
            do script "cd '$REPO_ROOT' && copilot --yolo"
            tell front window
                set custom title of tab 4 to "Copilot"
            end tell
        end tell
EOF

    echo "✓ All terminals launched in Terminal.app"

else
    # Linux - use tmux or gnome-terminal/xfce4-terminal
    if command -v tmux &> /dev/null; then
        # Use tmux if available
        tmux new-session -d -s "agents" -c "$REPO_ROOT"

        tmux send-keys -t "agents:0" "claude-yolo --model Opus" Enter
        tmux rename-window -t "agents:0" "Claude (Opus)"

        tmux new-window -t "agents" -c "$REPO_ROOT"
        tmux send-keys -t "agents:1" "claude-yolo --model Sonnet" Enter
        tmux rename-window -t "agents:1" "Claude (Sonnet)"

        tmux new-window -t "agents" -c "$REPO_ROOT"
        tmux send-keys -t "agents:2" "codex --model gpt-5.4 --yolo" Enter
        tmux rename-window -t "agents:2" "Codex (GPT-5.4)"

        tmux new-window -t "agents" -c "$REPO_ROOT"
        tmux send-keys -t "agents:3" "copilot --yolo" Enter
        tmux rename-window -t "agents:3" "Copilot"

        tmux select-window -t "agents:0"
        tmux attach-session -t "agents"

    else
        for app in gnome-terminal xfce4-terminal xterm; do
            if command -v $app &> /dev/null; then
                echo "Using $app to open terminals..."
                $app --title "Claude (Opus)" --working-directory="$REPO_ROOT" -e "bash -c 'claude-yolo --model Opus; bash'" &
                sleep 0.5
                $app --title "Claude (Sonnet)" --working-directory="$REPO_ROOT" -e "bash -c 'claude-yolo --model Sonnet; bash'" &
                sleep 0.5
                $app --title "Codex (GPT-5.4)" --working-directory="$REPO_ROOT" -e "bash -c 'codex --model gpt-5.4 --yolo; bash'" &
                sleep 0.5
                $app --title "Copilot" --working-directory="$REPO_ROOT" -e "bash -c 'copilot --yolo; bash'" &
                echo "✓ All terminals launched"
                break
            fi
        done
    fi
fi
