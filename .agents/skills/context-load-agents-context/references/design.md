# Load AGENTS Context — Design & Rationale

Reference documentation for the `context-load-agents-context` skill. This is the *why* and the
*how-it-works-internally*; the runtime contract (non-negotiables, invocation, hook registration)
lives in `../SKILL.md`. Agents do not need this file to run the skill — read it only when
modifying the skill or its script.

## System Context

```mermaid
sequenceDiagram
    participant AI as AI Agent
    participant Hook as load-agents-context.sh
    participant FS as Repo File System
    participant Ctx as Conversation Context

    AI->>Hook: PostToolUse (tool: Read|Edit, file_path)
    Hook->>Hook: Check tool name — exit 0 if not Read|Edit|Write
    Hook->>Hook: Resolve absolute path, detect git repo root
    Hook->>FS: Walk dir(file) → repo root, find *AGENTS.md per level
    FS-->>Hook: Candidate AGENTS.md paths (sorted, maxdepth 1 per dir)
    Hook->>Hook: Filter: skip auto-loaded dirs + session-tracker hits
    Hook->>Hook: Skill-on-path: rule file ⇒ manage-rule-system; *AGENTS.md ⇒ knowledge-conventional-contexts
    Hook->>FS: Read each new file (AGENTS.md + skill SKILL.md)
    Hook->>Ctx: Emit <context-auto-loaded> block via stdout
    Hook->>Hook: Append loaded paths to session tracker file
```

## Architecture Decisions

### LADR-001: PostToolUse over UserPromptSubmit
**Date:** 2026-05-14 | **Status:** Accepted

**Context:** `UserPromptSubmit` fires on every user message regardless of which files are in scope. `PostToolUse` fires only after a specific tool executes, so context is loaded only when the AI actually opens a file.

**Decision:** Use `PostToolUse` with `matcher: "Read|Edit"`.

**Consequences:** Context is injected *after* the first read of a file in a new directory. The AI has context before acting on the content, just not before the initial read call. Acceptable trade-off for targeted loading.

### LADR-002: Session file for deduplication
**Date:** 2026-05-14 | **Status:** Accepted

**Context:** Without dedup, the same `*AGENTS.md` is injected on every subsequent `Read`/`Edit` or explicit command call in that directory, bloating context linearly.

**Decision:** Write loaded absolute paths to `/tmp/.agents_ctx_${SESSION_ID}`. Explicit `--session-id` always wins. Otherwise, Codex CLI mode uses `CODEX_SESSION_ID` then `CODEX_THREAD_ID`; Copilot CLI mode uses `COPILOT_SESSION_ID` then `GITHUB_COPILOT_SESSION_ID`; Claude hook mode uses `CLAUDE_SESSION_ID`. All modes fall back to `$PPID`.

**Consequences:** Temp files accumulate in `/tmp/` but are trivially small and cleaned by the OS on reboot. If a session crashes and restarts with the same ID, the tracker prevents reloading — this is the desired behaviour.

### LADR-003: Walk to git root, not filesystem root
**Date:** 2026-05-14 | **Status:** Accepted

**Context:** Walking to `/` picks up unrelated `*AGENTS.md` files from parent repositories or home directories.

**Decision:** Use `git rev-parse --show-toplevel` from the file's directory (not CWD) to stop at the repo boundary. Fall back to a max-depth of 20 if `git` is unavailable.

**Consequences:** Requires `git` in `$PATH` for the boundary guard to work (standard assumption in development environments). Monorepos are handled correctly because the root is detected from the file's own location.

### LADR-004: Skill-resident script — thin hook wrapper
**Date:** 2026-05-14 | **Status:** Accepted

**Context:** Scripts could live entirely in `.agents/hooks/` or inside the skill folder. Either is workable.

**Decision:** Authoritative script lives at `.agents/skills/context-load-agents-context/scripts/load-agents-context.sh`. A one-line `exec` wrapper at `.agents/hooks/load-agents-context.sh` lets `settings.json` reference the conventional `.agents/hooks/` path.

**Consequences:** Transferring to another repo requires copying the skill folder, copying the hook wrapper, and adding one entry to `settings.json`. Single source of truth for the implementation; agent-agnostic invocation lives in one place.

### LADR-005: Scope-conditional rule injection — Superseded
**Date:** 2026-05-14 | **Status:** Superseded (2026-05-30)

**Context:** Originally the hook injected `.agents/rules-scoped/<scope>/*.instructions.md` only when an in-scope file was touched, to keep backend rules out of unrelated sessions.

**Superseded by:** Per-file frontmatter scoping. Rule files now live under `.agents/rules/` (with `backend/`, `git/`, `meta/` category subfolders) and carry `paths`/`globs`/`applyTo` so each AI tool filters applicability itself — Claude via `paths`, Cursor via `globs`+`alwaysApply`, Copilot via `applyTo`. The `.agents/rules-scoped/` directory and the hook's scope-injection block were removed. The hook no longer special-cases scope; it only performs the AGENTS.md ancestor walk and skill-on-path injection (LADR-006).

### LADR-006: Skill-on-path injection (rule files, AGENTS.md)
**Date:** 2026-05-14 | **Status:** Accepted

**Context:** Some guidance is only relevant when a specific kind of file is being edited — e.g., the `manage-rule-system` skill is useful when the agent edits `.agents/rules*/` content, and `knowledge-conventional-contexts-quality.instructions.md` is most useful when editing `*AGENTS.md`. Always-loading both bloats every session; loading neither means the agent reasons without the guidance when it most needs it.

**Decision:** The hook injects targeted SKILL.md / rule files based on the touched file's location or basename: editing under `.agents/rules*/` injects `manage-rule-system/SKILL.md`; editing `AGENTS.md` / `CLAUDE.md` / `GEMINI.md` / `*_AGENTS.md` injects the knowledge-conventional-contexts rule. Each is dedup-tracked per session.

**Consequences:** Modest token cost (one file per trigger, gated by dedup); large quality win because the guidance arrives precisely when needed. If a file qualifies for multiple triggers, all matching contexts inject (the cap is one per session per file).

## Key Behaviors

1. **Trigger filter**: In hook mode, exits 0 immediately for any tool that is not `Read` or `Edit`. In explicit skill mode, accepts `--file PATH` or a positional path.
2. **Path resolution**: Uses `cd "$(dirname ...)" && pwd` pattern to resolve relative paths correctly regardless of the AI agent's working directory.
3. **Repo root detection**: `git rev-parse --show-toplevel` is called from the file's directory, not the process CWD — handles nested repos and monorepos correctly.
4. **File pattern**: Matches `AGENTS.md` and `*_AGENTS.md` at `maxdepth 1` per directory. Does not recurse, so only the immediate directory's context is loaded at each level.
5. **Auto-loaded dir skip**: Paths matching `.agents/rules/`, `.ai/rules/`, `.claude/rules/`, `.cursor/rules/`, `.github/instructions/` are silently skipped.
6. **Session tracker**: `/tmp/.agents_ctx_${SESSION_ID}` — one absolute path per line, checked via `grep -qxF` (exact full-line match, no partial hits).
7. **Output envelope**: All emitted content is wrapped in `<context-auto-loaded>` tags for traceability. Each file is prefixed with `## Context: <relative-path>`.
8. **Tool agnostic**: Plain bash with `jq` as the only external dependency. Claude Code uses hook mode. Codex and Copilot use explicit skill invocation and session identifiers (`CODEX_THREAD_ID`/`CODEX_SESSION_ID`, `COPILOT_SESSION_ID`/`GITHUB_COPILOT_SESSION_ID`) for stable deduplication.
9. **Skill-on-path injection**: Touching a file under `.agents/rules/` / `.claude/rules/` / `.cursor/rules/` / `.github/instructions/` injects `.agents/skills/manage-rule-system/SKILL.md`. Touching `AGENTS.md` / `CLAUDE.md` / `GEMINI.md` / `*_AGENTS.md` injects `.agents/rules/meta/knowledge-conventional-contexts-quality.instructions.md`. Each is dedup-tracked per session. (Rule files themselves are auto-loaded by the AI tool and scoped per-file via frontmatter — the hook does not inject them.)

## Transferring to Another Repo

1. Copy `.agents/skills/context-load-agents-context/` to the target repo's skills folder (adjust path prefix if the repo uses `.ai/` instead of `.agents/`)
2. Copy `.agents/hooks/load-agents-context.sh` wrapper (adjust the path it `exec`s if needed)
3. Add the hook registration (see `../SKILL.md`) to the target repo's `settings.json`
4. Ensure `jq` and `git` are available in the environment
5. Optionally: remove any central context-index file or `Current Context File Map` table from the root AGENTS.md/CLAUDE.md
