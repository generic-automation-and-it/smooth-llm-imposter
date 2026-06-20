# AGENTS.md - AI Development Experience

🤖 AI Context: Unified AI development folder structure and best practices. Updated: 2026-04-03 Maintainer: Engineering Team

## 🎯 TL;DR

The `.agents` folder provides a tool-agnostic structure for AI-assisted development, with symbolic links (`.claude`, `.cursor`, `.codex`) ensuring compatibility across multiple AI coding tools without duplication or vendor lock-in.

## 📋 Overview

This is a unified AI development experience folder that centralizes skills, prompts, and configuration for AI-assisted coding tools.

**Scope:**
- In: Skill definitions, prompt templates, tool permissions, AI workflow orchestration
- Out: Tool-specific internal state (handled by individual tools), model weights, API credentials
- Depends: Git (for version control), bash/shell (for scripts), symbolic link support (Unix-like systems)

## 🏗️ Architecture

### Folder Structure

| Path | Purpose |
| :---- | :---- |
| `.agents/` | Root folder for all AI development tooling |
| `.agents/prompts/` | Reusable prompt templates (code review, architecture analysis) |
| `.agents/roles/` | Multi-agent role instructions (PO, Architect, QA, Backend/Frontend Engineer, Heimdall Reviewer) |
| `.agents/rules/` | Enforced AI development rules (workflow rules, coding standards) |
| `.agents/settings.json` | Tool permissions, compile/test commands, hook registrations — every script in `.agents/hooks/` MUST be registered here or it silently never fires (#32) |
| `.agents/skills/` | Executable skills (multi-file workflows) — flat dirs, category-prefixed folder names |
| `.agents/skills/agile-github-task-from-diff/` | Create a GitHub Task (sub-issue) from the current git diff vs main |
| `.agents/skills/ai-brain-dump/` | Listen-first capture session; synthesize on request |
| `.agents/skills/ai-mansplain/` | Reformat this turn's reply into terse, high-density output with a TL;DR |
| `.agents/skills/ai-review/` | Vendored `/ai-review` consumer skill (parse AI PR review → apply fix/skip); generator stays remote via `.github/workflows/pipeline-code-review-report.yml` |
| `.agents/skills/ai-template-sync/` | UPSERT the smooth-devex-template agentic scaffold into an existing repo |
| `.agents/skills/context-load-context/` | Load or create functional `*_AGENTS.md` context files |
| `.agents/skills/context-load-agents-context/` | Load ancestor AGENTS.md context for a target file |
| `.agents/skills/git-commit/` | Commit with conventional format |
| `.agents/skills/git-commit-push/` | Commit and push to remote |
| `.agents/skills/git-commit-push-pr/` | Commit, push, and create/update PRs |
| `.agents/skills/git-sync/` | Sync with main (optionally auto-resolve conflicts) |
| `.agents/skills/manage-rule-system/` | Create/update rule files in `.agents/rules/` |
| `.agents/templates/` | Document templates (AGENTS.md, README.md, work task promote templates) |
| `.claude` → `.agents` | Symbolic link for Claude Code compatibility |
| `.codex` → `.agents` | Symbolic link for OpenAI Codex compatibility |
| `.cursor` → `.agents` | Symbolic link for Cursor AI compatibility |
| `CLAUDE.md` → `AGENTS.md` | Symbolic link alias for Claude-compatible root context discovery |
| `GEMINI.md` → `AGENTS.md` | Symbolic link alias for Gemini-compatible root context discovery |
| `.github/instructions` → `../.agents/rules` | Symbolic link exposing rule files at `.github/instructions/**.instructions.md` for GitHub Copilot path-specific instructions |

### Tool Compatibility Matrix

| Tool | Access Method | Status |
| :---- | :---- | :---- |
| **Claude Code** | Via `.claude` symlink | ✅ Active |
| **GitHub Copilot** | Reads `.github/instructions/` directly — a **real directory** (path-specific `*.instructions.md`); root `AGENTS.md` for repo-wide context | ✅ Active |
| **Cursor AI** | Via `.cursor` symlink | ✅ Active |
| **OpenAI Codex** | Via `.codex` symlink | ✅ Active |
| **Gemini** | Via `GEMINI.md` symlink | ✅ Compatible |
| **Aider** | Direct `.agents` access (CLI) | ✅ Compatible |

## 📐 Architecture Decisions (Lightweight ADRs)

### LADR-001: Agnostic .agents Folder Structure

- **Date**: 2026-02-12
- **Status**: Accepted
- **Context**: Project was using `.claude` folder, but team wanted to support multiple AI coding tools without duplicating configuration or creating vendor lock-in
- **Decision**: Create tool-agnostic `.agents` folder as single source of truth, with symbolic links for tool-specific compatibility
- **Consequences**:
  - Single configuration folder to maintain
  - Easy to add support for new AI tools (just create symlink)
  - Backward compatible with existing `.claude` references
  - Requires symbolic link support (standard on Unix/Linux/macOS)

### LADR-002: Symbolic Link Strategy for Backward Compatibility

- **Date**: 2026-02-12
- **Status**: Accepted
- **Context**: Existing scripts, documentation, and workflows reference `.claude` paths explicitly
- **Decision**: Use symbolic links (`.claude` → `.agents`, `.cursor` → `.agents`, `.codex` → `.agents`) to maintain backward compatibility while migrating to agnostic structure
- **Consequences**:
  - Zero-downtime migration (existing references continue working)
  - Tools automatically access unified configuration
  - Symbolic links are committed to git

### LADR-003: Git Ignore Strategy

- **Date**: 2026-02-12
- **Status**: Accepted
- **Context**: Some AI tools generate local state files that should not be committed
- **Decision**:
  - Commit `.agents` folder structure and configuration to git
  - Commit symlinks to git for zero-setup developer experience
  - Ignore tool-specific local state: `.agents/settings.local.json`
- **Consequences**:
  - Clean git history without local state pollution
  - Symlinks available immediately after clone

### LADR-004: Rule Files Physically Located in `.github/instructions` (Symlink Inversion)

- **Date**: 2026-06-06
- **Status**: Accepted
- **Context**: GitHub Copilot's Coding Agent / Code Review runs on github.com against a server-side checkout. With the rule files living in `.agents/rules` and `.github/instructions` as a symlink → `../.agents/rules`, Copilot did not load the path-scoped instructions — its instruction loader does not reliably traverse a symlinked directory server-side. Claude Code, Codex, and Cursor run locally where symlink resolution is never a problem.
- **Decision**: Invert the symlink. The rule files now physically live in `.github/instructions/` (a real, committed directory Copilot reads natively). `.agents/rules`, `.claude/rules`, and `.cursor/rules` are symlinks resolving back to it (`.agents/rules → ../.github/instructions`; the others reach it via `.claude`/`.cursor` → `.agents`).
- **Consequences**:
  - Copilot reads rules with no server-side symlink resolution required.
  - Local agents resolve the same files through a two-hop symlink (`.claude/rules → .agents/rules → .github/instructions`) — fine on local filesystems.
  - `.agents/` remains the conceptual hub for skills/hooks/AGENTS.md, but the rule *content* now lives under `.github/`; every path reference to `.agents/rules/...` still resolves via the symlink, so hooks and skill docs did not need path edits.
  - `ai-template-sync` provisions new repos with the inverted topology (real `.github/instructions` + `.agents/rules` symlink).
  - **Open item**: this fixes *delivery* (Copilot can now see the rules), not *enforcement* (Copilot still treats them as soft guidance with no phase-gate hooks) and assumes Copilot does not recurse symlinked dirs — verify Copilot now honours subfolder rules (`backend/`, `git/`, `meta/`) on a live run.

## 📊 Setup Instructions

**The rule files physically live in `.github/instructions/` (a real, committed directory). Symlinks (`.claude`, `.codex`, `.cursor`, `CLAUDE.md`, `GEMINI.md`, and `.agents/rules → ../.github/instructions`) are committed to git and available immediately after clone. GitHub Copilot reads path-specific rules natively from `.github/instructions/**.instructions.md` (no symlink resolution required) and repo-wide context from root `AGENTS.md`, so no setup script is required.**

```bash
# Verify links are present after clone
ls -la | grep -E '(\.claude|\.codex|\.cursor)'
# Expected output:
# lrwxr-xr-x ... .claude -> .agents
# lrwxr-xr-x ... .codex -> .agents
# lrwxr-xr-x ... .cursor -> .agents
```

**Optional: Run setup script to recreate symlink aliases if needed:**
```bash
# Mac/Linux
./.agents/setup/scripts/agents-setup.sh

# Windows (Administrator)
./.agents/setup/scripts/agents-setup.ps1
```

## 📝 Changelog

| Date | Change | Reason |
| :---- | :---- | :---- |
| 2026-05-30 | Initial version. | |
| 2026-06-10 | Registered orphaned `UserPromptSubmit` hooks (`worktask-create.sh`, `agentmd-create-update.sh`, `knowledge-rule-enforce.sh`) in `settings.json` — they existed on disk but never fired. | #32 |
| 2026-06-20 | Vendored `/ai-review` consumer skill (minimal install); added thin caller `.github/workflows/pipeline-code-review-report.yml` (uses upstream `@main`, provider OpenAI); permitted skill + script in `settings.json`. | |
