# AI Skills

Self-contained skills for Claude Code, GitHub Copilot, and OpenAI Codex providing specialized workflows and tools.

Skills live **flat**, one directory per skill directly under `.agents/skills/`. Each folder name is **category-prefixed** (`agile-`, `ai-`, `context-`, `git-`) so the listing groups by category when sorted. The prefix is the only grouping mechanism — there are no category subfolders (Claude Code discovers skills exactly one level under `.claude/skills/`).

## Quick Reference

| Skill | Purpose | Usage |
|-------|---------|-------|
| **agile-github-task-from-diff** | Create a GitHub Task (sub-issue) from the current git diff vs main | `/agile-github-task-from-diff` |
| **ai-brain-dump** | Listen-first capture session; synthesize on request | `/ai-brain-dump [--oktoask] [--thinking] [--oktoreaddocs] [--oktowebsearch]` |
| **ai-mansplain** | Reformat this turn's reply into terse, high-density output with a TL;DR | `/ai-mansplain` |
| **ai-review** | Analyse an AI PR review and apply per-issue fix/skip decisions | `/ai-review <pr> [N=fix\|N=skip …]` |
| **ai-template-sync** | UPSERT smooth-devex-template scaffold into an existing repo | `/ai-template-sync` |
| **context-load-agents-context** | Load ancestor AGENTS.md context for a file | `/context-load-agents-context` |
| **context-load-context** | Load domain context before implementation | `/context-load-context auth` |
| **git-commit** | Commit with conventional format | `/git-commit [--mansplain]` |
| **git-commit-push** | Commit and push to remote | `/git-commit-push [--mansplain]` |
| **git-commit-push-pr** | Commit, push, and create/update PR | `/git-commit-push-pr [--mansplain]` |
| **git-commit-review-push** | Commit, append `/ai-review`, and push | `/git-commit-review-push [--issue <number>]` |
| **git-sync** | Sync with main (optionally auto-resolve conflicts) | `/git-sync` |
| **manage-rule-system** | Create/update rule files in `.agents/rules/` | `/manage-rule-system` |

### ai-brain-dump switches

Default (no switch) is pure silent listen-first — no questions, no tools — until you ask it to synthesize.
Opt-in switches relax that, at different token costs (see `ai-brain-dump/README.md` for the full breakdown):

| Switch | Effect | Cost |
|--------|--------|------|
| _(none)_ | Capture silently; never ask, never browse | baseline |
| `--oktoask` | Ask sparse, non-blocking, tool-free clarifying questions on genuine blockers | small |
| `--thinking` | Make questioning liberal (ask on any unclear/detail gap); implies `--oktoask` | moderate |
| `--oktoreaddocs` | May read local code/docs to ground a question; implies `--oktoask` | large |
| `--oktowebsearch` | May web-search to ground a question; implies `--oktoask` | large |

The tool switches (`--oktoreaddocs`, `--oktowebsearch`) re-enable the file/web payload bloat the
listen-first default avoids — use deliberately.

### git-commit / git-commit-push / git-commit-push-pr switches

The `--mansplain` switch suppresses all interactive questions across the entire commit chain. When passed, the agent uses its best judgment on commit grouping, message selection, and PR title — it never stops to ask.

| Switch | Effect |
|--------|--------|
| _(none)_ | Default — ask for clarification when grouping is unclear or a conforming message cannot be determined |
| `--mansplain` | **No questions asked.** Agent decides everything autonomously and proceeds without confirmation |

`--mansplain` is forwarded automatically through the skill chain: `git-commit-push-pr` → `git-commit-push` → `git-commit`.

### git-commit-review-push switches

`git-commit-review-push` is a sibling push skill that adds `/ai-review` to the final commit body instead of opening a PR.

| Switch | Effect |
|--------|--------|
| `--issue <number>` | Rename the local branch to the configured issue-number convention before pushing when needed |

## Model Selection

Skills are classified by complexity tier. Each SKILL.md carries a `models` frontmatter block with the recommended model per tool. When a skill is invoked as a sub-agent, use the model from its `models` block.

| Complexity | Claude Code | GitHub Copilot | OpenAI Codex |
|-----------|-------------|----------------|--------------|
| **low** | `haiku` | `gpt-5.4-mini` | `gpt-5.4-mini` |
| **medium** | `sonnet` | `auto` | `gpt-5.4` |
| **high** | `opus` | `auto` | `gpt-5.5` |

### Skill complexity classification

| Skill | Complexity | Rationale |
|-------|-----------|-----------|
| **context-load-context** | low | File discovery and loading; no deep reasoning |
| **context-load-agents-context** | low | Script-driven file traversal; no deep reasoning |
| **git-commit** | low | Diff review + conventional commit; straightforward |
| **git-sync** | low | Fetch + merge; straightforward git operations |
| **git-commit-push** | medium | Branch rename logic + upstream tracking |
| **git-commit-push-pr** | medium | PR template authoring + state management |
| **git-commit-review-push** | medium | Conventional chunking, optional branch rename, and full-review trigger |
| **agile-github-task-from-diff** | medium | Diff classification + issue authoring |
| **manage-rule-system** | medium | Cross-tool frontmatter authoring |
| **ai-mansplain** | low | Single-turn reply reformatting; no tools or deep reasoning |
| **ai-review** | medium | Review parsing + fix/skip code edits across multiple files |
| **ai-brain-dump** | high | Multi-turn synthesis + deep requirement reasoning |
| **ai-template-sync** | high | Interactive multi-turn Q&A + conditional file sync across tools |

### Sub-skill invocation model guidance

When a skill invokes another skill as a sub-agent, use the sub-skill's model tier:

- **git-commit-push** → invokes **git-commit** (low): use `haiku` / `gpt-5.4-mini` / `gpt-5.4-mini`
- **git-commit-push-pr** → invokes **git-commit-push** (medium): use `sonnet` / `auto` / `gpt-5.4`
- **git-commit-review-push** → performs commit and push itself (medium): use `sonnet` / `auto` / `gpt-5.4`

## Naming & Ordering

Skills are flat under `.agents/skills/`; the category lives in the folder-name prefix so a sorted listing groups by category:

| Prefix | Skills |
|--------|--------|
| `agile-` | `agile-github-task-from-diff` |
| `ai-` | `ai-brain-dump`, `ai-mansplain`, `ai-review`, `ai-template-sync` |
| `context-` | `context-load-agents-context`, `context-load-context` |
| `git-` | `git-commit`, `git-commit-push`, `git-commit-push-pr`, `git-commit-review-push`, `git-sync` |
| _(none)_ | `manage-rule-system` |

A skill's folder name MUST equal its `name:` frontmatter (this is the slash-command name). When adding a skill, pick the prefix of its category and keep the folder one level under `.agents/skills/`.

## About Skills

Each skill is a directory containing:
- **SKILL.md** — The skill definition with workflow steps and `models` frontmatter
- **AGENTS.md** — Maintenance context for agents *modifying* the skill (coupling, rationale, drift hazards) per `.agents/rules/meta/knowledge-conventional-contexts-quality.instructions.md`
- **agents/openai.yaml** — OpenAI Codex agent registration with model specification
- **scripts/** — Helper scripts (if applicable)
- **references/** — Reference documentation (if applicable)

Skills are tool-agnostic and work across Claude Code, GitHub Copilot, and OpenAI Codex.
