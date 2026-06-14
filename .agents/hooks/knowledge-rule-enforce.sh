#!/bin/bash
# Hook: Enforce AGENTS.md quality rules when user requests enforcement on an existing file
# Event: UserPromptSubmit
# Triggers: "agent.md rule", "agents.md rule", "context rule", "knowledge rule" (case insensitive)

PROMPT=$(jq -r '.prompt // empty')
[ -z "$PROMPT" ] && exit 0

if echo "$PROMPT" | grep -qiE 'agents?\.md rule|context rule|knowledge rule'; then
  cat <<'EOF'
AGENTS.md Quality Enforcement Rules (from knowledge-conventional-contexts-quality.instructions.md):

You MUST enforce these rules when reviewing or rewriting an AGENTS.md file:

## Required Structure (in this order — omit empty sections except Changelog)
1. TL;DR — One line: what it does + most important constraint
2. Non-Negotiables — Only items an AI coder would plausibly get wrong
3. System Context — 2-4 sentences + diagrams:
   - C4Context: for services/workers with external integrations (APIs, DBs, queues, third-party)
   - sequenceDiagram: for handlers/services with 3+ steps OR side effects (email, SignalR, external calls)
   - erDiagram: for DbContext/repos with 3+ related entities with non-obvious relationships
4. Architecture Decisions — LADR format (LADR-NNN, Date, Status, Context, Decision, Consequences). Only where rejected alternative looks reasonable to AI.
5. Key Behaviors — Non-obvious behaviors NOT apparent from source code
6. Test References — L0/L1 tier + sub-folder path (backend only; omit if no tests)
7. Quality Constraints — Feature-specific NFRs beyond project baseline (omit if none)
8. Migration Plans — Planned migrations/deprecations affecting new code (omit if none)
9. Changelog — Always include header. Format: | Date | Change | Ref |

## Value Test — apply to every line:
1. Bug prevention: Would AI write worse code without this?
2. Decision quality: Would AI make a worse architectural choice without this?
3. System understanding: Does this reveal system boundaries/constraints not in source?
If NONE apply → remove it.

## Anti-Patterns (MUST remove):
- Empty sections with "N/A" or "None" — omit entirely
- `(src: path)` annotations
- File listings as "components"
- Restating what source code shows clearly
- Generic boilerplate (e.g., "Required secrets: AWS_ACCESS_KEY_ID")
- Business value / problem-solution-impact blocks
- Validation checklists that restate expected behavior
- "see other file" cross-references as section content
- Duplicating root AGENTS.md content

## Drift Minimization:
- If code behavior is documented in AGENTS.md, the file MUST be updated in the same PR
- Stale context is WORSE than no context — it causes AI to code against outdated assumptions
- Every update MUST add a changelog entry

Apply all rules above to the file referenced in the prompt. Output the corrected/reviewed AGENTS.md.
EOF
fi

exit 0
