# ai-mansplain — AGENTS.md

## TL;DR

Single-turn output reformatter (pure prompt, no tools); applies to the current reply only and must never truncate code/command payloads while compressing prose.

## Key Behaviors

- **Naming collision with the git-skill switch:** `--mansplain` on `git-commit`/`git-commit-push`/`git-commit-push-pr` means "suppress interactive questions, decide autonomously" — it does NOT invoke this skill or its output format. Don't merge or cross-reference the two when editing either side.
- The TL;DR block (Value/Holes/Ignored) is the skill's contract with downstream readers — edits that drop it break consumers who scan only that block.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-06-12 | Initial version. | |
