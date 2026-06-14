# AI Agent Tools - Multi-Terminal Launcher for Windows
# Opens separate terminal windows for different AI tools and models

# Array of terminal configurations: @(Name, Command)
$terminals = @(
    @("Claude (Opus)", "claude-yolo --model Opus"),
    @("Claude (Sonnet)", "claude-yolo --model Sonnet"),
    @("Codex (GPT-5.4)", "codex --model gpt-5.4 --yolo"),
    @("Copilot", "copilot --yolo")
)

# Get the repo root
$repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))

Write-Host "Launching AI Agent Tool terminals..." -ForegroundColor Cyan
Write-Host ""

foreach ($terminal in $terminals) {
    $name = $terminal[0]
    $command = $terminal[1]

    Write-Host "Opening: $name" -ForegroundColor Green

    # Open PowerShell window with custom title and execute command
    Start-Process powershell -ArgumentList "-NoExit", "-Command", "Set-Location '$repoRoot'; `$Host.UI.RawUI.WindowTitle='$name'; $command"
}

Write-Host ""
Write-Host "[OK] All terminals launched" -ForegroundColor Green
