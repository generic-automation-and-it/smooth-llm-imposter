# ai-template-sync — AGENTS.md

## TL;DR

UPSERT distributor for the agentic scaffold: all decisions (tools, overwrite scope, dotnet) stay with the agent; all file mechanics live in `scripts/sync.sh` / `sync.ps1`, which must stay behaviorally identical.

## Non-Negotiables

- **Keep `sync.sh` and `sync.ps1` in lockstep.** Every flag and section added to one MUST be added to the other in the same change — Windows consumers get the `.ps1` path only.
- **Never add deletion of landing-only files** to any section or mode. The contract is UPSERT; `--overwrite global` means "replace same-named files", never "mirror the template".
- **Dotnet files are add-only** (`Section E`) — no overwrite path may ever be introduced, even behind a flag.

## Architecture Decisions

- **LADR-001** (2026-06-12, accepted): `--rules-only` is the supported channel for distributing this repo's rule system to other repositories (a "rules package manager"). *Context:* no AI tool has a native cross-tool rules marketplace; Claude plugins/Copilot org-instructions each cover one tool. *Decision:* extend the existing sync script (which already owns the symlink contract and overwrite safety) instead of per-tool marketplace wrappers; pin versions via `--template-ref`. *Consequence:* the Phase 2 rules-layout pre-flight is enforced **in the script** for this mode (refuses a real `.agents/rules` dir, exit 65); marketplace wrappers, if ever built, must shell out to `sync.sh --rules-only` rather than reimplement copying.

## Key Behaviors

- Section D (`copilot` in `--tools`) replaces `.github/instructions/` and re-points the `.agents/rules` symlink — this is the one destructive-looking step, which is why SKILL.md Phase 2 runs even in Global mode and why the script refuses when `.agents/rules` is a real directory.
- `--template-ref` falls back from `git clone --branch` (tags/branches) to full-clone + detached checkout (SHAs); a failed shallow attempt is silently retried, so a slow pinned clone is expected for SHA pins.
- `sync.ps1` ends rules-only mode with `return` inside `try` — the `finally` block still removes the temp clone; don't convert it to `exit`.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-06-12 | Initial version. Added `--rules-only` / `--template-ref` rules-distribution mode to both scripts; Phase 0 compressed to use `--template-url` self-acquire. | |
