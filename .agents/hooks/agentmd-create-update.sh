#!/bin/bash
# Hook: Enforce quality rules + template when creating or updating an AGENTS.md file
# Event: UserPromptSubmit
# Triggers: "update agent.md" / "update agents.md",
#           "create agent.md" / "create agents.md",
#           "create an agent.md" / "create an agents.md" (case insensitive)

PROMPT=$(jq -r '.prompt // empty')
[ -z "$PROMPT" ] && exit 0

if echo "$PROMPT" | grep -qiE '(update|create an?)\s+agents?\.md'; then
  cat <<'EOF'
AGENTS.md Creation/Update Rules (from knowledge-conventional-contexts-quality.instructions.md + AI_WORKTASK_PROMOTE_STANDALONE_TEMPLATE.md):

## Template to use
Base all new AGENTS.md files on: .agents/templates/AI_WORKTASK_PROMOTE_STANDALONE_TEMPLATE.md
(Root CLAUDE.md/AGENTS.md is exempt from template requirements)

## Required Structure (in this order — omit empty sections except Changelog)
1. TL;DR — One line: what it does + most important constraint
2. Non-Negotiables — Only items an AI coder would plausibly get wrong
3. System Context — 2-4 sentences + diagrams where applicable:
   - C4Context (Mermaid): services/workers with external integrations (APIs, DBs, queues)
   - sequenceDiagram (Mermaid): handlers/services with 3+ steps OR any side effects
   - erDiagram (Mermaid): DbContext/repos with 3+ related entities with non-obvious relationships
4. Architecture Decisions — LADR format (LADR-NNN, Date, Status, Context, Decision, Consequences)
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

## Anti-Patterns (MUST NOT include):
- Empty sections with "N/A" or "None" — omit entirely
- `(src: path)` annotations
- File listings as "components"
- Restating what source code shows clearly
- Generic boilerplate
- Business value / problem-solution-impact blocks
- Validation checklists that restate expected behavior
- "see other file" cross-references as section content
- Duplicating root AGENTS.md content

## Drift Rule
Every AGENTS.md update MUST add a changelog entry. Stale context is worse than no context.

Proceed: read the referenced file (if it exists), apply the template structure, enforce the rules above, and output the complete AGENTS.md content.
EOF
fi

exit 0
