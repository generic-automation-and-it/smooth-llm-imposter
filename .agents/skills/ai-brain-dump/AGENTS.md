# ai-brain-dump — AGENTS.md

## TL;DR

Pure-prompt behavioral skill (no scripts): a listen-first capture session whose entire value is in NOT acting — never implement, modify, or synthesize until explicitly asked.

## Non-Negotiables

- **A braindump is never permission to implement.** Even with all switches on, only *grounding* (reading/searching) is widened — the no-modify rule holds until the user asks to synthesize.
- **Don't "optimize" the SKILL.md by deduplicating switch semantics.** The switch rules are intentionally restated in Modes, Listen, and Guardrails — reinforcement against the model's drift toward premature questioning/action is the point, not redundancy.

## Architecture Decisions

- **LADR-001** (2026-05, accepted): Default mode is tool-free, opt-in switches relax it. *Context:* file/web tool payloads are injected into context and re-billed every subsequent turn of a long capture session. *Decision:* default forbids tools; `--oktoreaddocs`/`--oktowebsearch` opt back in. *Consequence:* any edit that adds default tool use destroys the skill's cost profile — see the README cost table before changing switch behavior.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-06-12 | Initial version. | |
