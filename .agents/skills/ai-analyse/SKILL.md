---
name: ai-analyse
switches:
  - "`--analyse <review-ref>` - inspect the latest gate review or supplied review reference and recommend FIX/SKIP decisions for low/medium findings only."
  - "`--execute <pr>` - apply FIX decisions for low/medium findings and print a FIX/SKIP summary table."
  - "`--source=opencode` - force OpenCode Review Report parsing; this is the default in CI."
description: Autonomous low/medium AI review fixer. Reads an OpenCode Review Report, ignores Critical/High findings, applies only safe Medium/Low fixes, and emits a FIX/SKIP summary. In headless CI it edits files and prints the table only; the workflow owns commit, push, and PR comment posting.
allowed-tools:
  - Bash(.agents/skills/ai-analyse/scripts/*)
  - Bash(${CLAUDE_PLUGIN_ROOT}/.agents/skills/ai-analyse/scripts/*)
  - Bash(.agents/skills/ai-review/scripts/copilot-review.sh:*)
  - Bash(${CLAUDE_PLUGIN_ROOT}/.agents/skills/ai-review/scripts/copilot-review.sh:*)
  # The three git entries below are for LOCAL /ai-analyse --execute invocations
  # only (e.g. from Claude Code). In headless CI they are NON-BINDING: the
  # `analyse` opencode agent in ai-review-report/assets/opencode.json denies bash,
  # and the pipeline-ai-analyse.yml workflow owns commit/push/PR-comment (see the
  # CI Contract below). Do not rely on these for the autonomous loop.
  - Bash(git add:*)
  - Bash(git commit:*)
  - Bash(git push:*)
models:
  claude: sonnet
  copilot: auto
  codex: gpt-5.4
---

# AI Analyse

Autonomously process low/medium findings from the OpenCode Review Report.

> **Script location.** Every `.agents/skills/ai-analyse/...` path in this document assumes the skill is installed in the repository. When this skill runs from the Claude Code plugin (`smooth-ai-review`), substitute `${CLAUDE_PLUGIN_ROOT}/.agents/skills/ai-analyse` for `.agents/skills/ai-analyse` in script paths.

## Invocation

The skill is invoked as `/ai-analyse <args>`.

Modes:

- `--analyse <review-ref>`: fetch the relevant PR review, parse only `### 🟡 Medium Priority Issues` and `### 🔵 Low Priority / Nitpicks`, and emit a recommendation table. Never recommend action for `### 🔴 Critical Issues` or `### 🟠 High Priority Issues`.
- `--execute <pr>`: apply fixes for rows marked FIX, keep edits scoped to listed low/medium items, and print the final FIX/SKIP markdown table.

## CI Contract

The GitHub Actions workflow invokes this skill headlessly by inlining this `SKILL.md` into an `opencode run --agent analyse` prompt. In CI:

1. Edit files only for gate-authored low/medium findings supplied in the prompt.
2. Do not run `git`, do not commit, and do not push.
3. Print a markdown table to stdout with the exact columns:

| # | Decision | Priority | File | Summary | Reason |
|---|----------|----------|------|---------|--------|

4. Use `FIX` only when the change is mechanical, directly supported by the listed finding, and low risk.
5. Use `SKIP` when the finding is speculative, already addressed, unclear, requires product judgment, would require broader refactoring, or touches Critical/High behavior.

The workflow performs deterministic commit, push, and PR comment posting after the model exits.

## Decision Rules

- Known intentional pattern: `SKIP`
- AI hallucination or stale review text: `SKIP`
- Genuine bug or logic error in a low/medium finding: `FIX`
- Real simplification with no trade-off: `FIX`
- Speculative / "consider" language: `SKIP`
- Any Critical or High finding, even if included in suggested fixes: `SKIP`

## Guardrails

- Never touch 🔴 Critical or 🟠 High findings.
- Never add a `/ai-review` marker.
- Keep every edit scoped to the supplied low/medium review text.
- Do not invent findings beyond the supplied review sections.
- Prefer minimal edits over refactors.
- The auto-fix commit message is owned by the workflow and carries `[ai-analyse]`.
- If no safe file edit is possible, print SKIP rows and leave the working tree unchanged.
