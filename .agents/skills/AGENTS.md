# Skills Ownership

## TL;DR

SmoothLlmImposter owns the two `imposter-*` skills; standard development skills come from upstream tooling rather than local copies.

## Non-Negotiables

- Only the skills marked OWNED below are maintained by this repository. Do not treat other vendored skills as repository-owned.
- Keep each owned skill and its `.marketplace/` mirror synchronized when changing definitions, helper scripts, registrations, or maintenance context. Consumers install the mirror; updating only the active skill leaves them on stale behavior.
- Do not reintroduce the removed generic skills as repository-maintained copies. Recommend the standard global installation in `COLLABORATION.md` instead.
- Keep workflow steps and command contracts in `SKILL.md`; skill-local `AGENTS.md` files carry maintenance hazards, not duplicate implementations.

## Key Behaviors

| Ownership | Skill path | Responsibility |
|---|---|---|
| OWNED | `.agents/skills/imposter-newsession` | Mint and verify caller-to-synthetic session mappings on the router. |
| OWNED | `.agents/skills/imposter-who` | Probe resolved upstream routing without an upstream model call. |

The hidden `.marketplace/` directory is an installable mirror, not another active skill collection. Standard global tooling must coexist with these router-specific skills. Unlisted retained skills remain upstream-owned; their presence does not expand this ownership list.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-10-21 | Established the owned skill boundary and marketplace parity requirement; moved standard-tooling setup to the collaboration guide and removed the listed non-owned skill copies. | |
