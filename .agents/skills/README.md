# AI Skills

Self-contained skills for Claude Code, GitHub Copilot, and OpenAI Codex providing specialized workflows and tools.

Skills live **flat**, one directory per skill directly under `.agents/skills/`. Each folder name is **category-prefixed** (`agile-`, `ai-`, `imposter-`) so the listing groups by category when sorted. The prefix is the only grouping mechanism — there are no category subfolders (Claude Code discovers skills exactly one level under `.claude/skills/`).

Only `imposter-newsession` and `imposter-who` are **OWNED** by this repository; see
[`AGENTS.md`](AGENTS.md) for the ownership boundary and marketplace-mirror requirements.
Other retained skills are upstream-owned. Install standard development skills, rules, and hooks globally
using the recommendation in [Collaboration](../../COLLABORATION.md#recommended-standard-ai-tooling).

## Quick Reference

| Skill | Purpose | Usage |
|-------|---------|-------|
| **agile-github-task-from-diff** | Create a GitHub Task (sub-issue) from the current git diff vs main | `/agile-github-task-from-diff` |
| **ai-brain-dump** | Listen-first capture session; synthesize on request | `/ai-brain-dump [--oktoask] [--thinking] [--oktoreaddocs] [--oktowebsearch]` |
| **ai-review** | Analyse an AI PR review and apply per-issue fix/skip decisions | `/ai-review <pr> [N=fix\|N=skip …]` |
| **imposter-who** | Probe the imposter router for the resolved upstream model behind the current harness | `/imposter-who [--model gpt-5.5]` |
| **imposter-newsession** | Mint a synthetic upstream session id on the imposter router and map it to the caller | `/imposter-newsession --session caller-id [--model gpt-5.5]` |

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
| **agile-github-task-from-diff** | medium | Diff classification + issue authoring |
| **imposter-who** | low | Single non-streaming curl; no deep reasoning |
| **imposter-newsession** | low | Single non-streaming curl; no deep reasoning |
| **ai-review** | medium | Review parsing + fix/skip code edits across multiple files |
| **ai-brain-dump** | high | Multi-turn synthesis + deep requirement reasoning |

## Naming & Ordering

Skills are flat under `.agents/skills/`; the category lives in the folder-name prefix so a sorted listing groups by category:

| Prefix | Skills |
|--------|--------|
| `agile-` | `agile-github-task-from-diff` |
| `ai-` | `ai-brain-dump`, `ai-review` |
| `imposter-` | `imposter-who`, `imposter-newsession` |

A skill's folder name MUST equal its `name:` frontmatter (this is the slash-command name). When adding a skill, pick the prefix of its category and keep the folder one level under `.agents/skills/`.

## About Skills

Each skill is a directory containing:
- **SKILL.md** — The skill definition with workflow steps and `models` frontmatter
- **AGENTS.md** — Maintenance context for agents *modifying* the skill (coupling, rationale, drift hazards) per `.agents/rules/meta/knowledge-conventional-contexts-quality.instructions.md`
- **agents/openai.yaml** — OpenAI Codex agent registration with model specification
- **scripts/** — Helper scripts (if applicable)
- **references/** — Reference documentation (if applicable)

Skills are tool-agnostic and work across Claude Code, GitHub Copilot, and OpenAI Codex.
