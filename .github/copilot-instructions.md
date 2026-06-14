# GitHub Copilot Instructions

These are the authoritative custom instructions for GitHub Copilot in this repository.

## AI Coder Instructions

- **Load and follow [`AGENTS.md`](../AGENTS.md)** as the single source of project context, conventions, and coding rules. Treat it as your primary instruction set.
- **Ignore `CLAUDE.md`** — it is a Claude Code-specific entry point and not intended for Copilot.
- **Ignore `.claude/**`** — Claude Code-specific configuration, hooks, and skills.
- **Ignore `.codex/**`** — OpenAI Codex-specific configuration.

`AGENTS.md` references shared rule files which physically live in `.github/instructions/` (a real directory). Copilot reads them natively from `.github/instructions/`, scoped per-file through their `applyTo` frontmatter — no symlink resolution required. The local-agent paths `.agents/rules`, `.claude/rules`, and `.codex/rules` are symlinks pointing back to `.github/instructions/`, so all tools share one source of truth. Follow the applicable rules when their `applyTo` glob matches the file you are editing.

When project context, conventions, or coding rules in `AGENTS.md` (or its referenced rule files) conflict with anything in the ignored files above, **`AGENTS.md` always wins.**
