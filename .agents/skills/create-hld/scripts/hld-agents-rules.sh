#!/usr/bin/env bash
# Skill: create-hld
# Emits the compacted quality rules for an HLD-folder AGENTS.md.
#
# This is the HLD VARIANT of the general AGENTS.md rules. It deliberately OMITS
# the System Context / C4 / architecture-diagram section, because in an HLD the
# architecture lives in diagrams/ (driven by SKILL.md), never in AGENTS.md.
#
# Shipped inside the skill so it is self-contained across agents: Codex and
# other tools that do not run Claude Code hooks can still read these rules by
# executing this script. The repo's global knowledge-rule-enforce.sh hook
# remains the enforcer for general (non-HLD) AGENTS.md files.

set -euo pipefail

cat <<'EOF'
HLD AGENTS.md Quality Rules (design-only variant — NO architecture section)

The AGENTS.md at the root of an HLD folder is the AI-coder context document.
Architecture diagrams belong in diagrams/, NOT here. Apply these rules:

## Required Structure (in order — omit any empty section except Changelog)
1. H1 + first line: "# AGENTS.md - <Initiative>" then "AI Context: HLD for <name>. Updated: YYYY-MM-DD"
2. TL;DR — One line: what this HLD covers + where intent (README), decisions (ladrs/), and quality spec (nfrs/) live.
3. Non-Negotiables — Only things an AI coder building against this design would plausibly get wrong. Examples:
   - "Do not collapse <X> into <Y> — the split is intentional (LADR-NN)."
   - "LADRs are Draft/Prototype — flag deviations, do not silently override."
4. Architecture Decisions — Table summarising LADRs an AI coder must respect.
   Columns: | LADR | Decision | Why it matters |. Link to ./ladrs/ once. Only LADRs whose violation produces wrong code.
5. Key Behaviors — Non-obvious runtime/operational truths not apparent from a design read.
6. Quality Constraints — Pointer to ./nfrs/ + any feature-specific constraint that changes how code is written. No measurable targets duplicated from NFR files.
7. Migration Plans — Planned splits/deprecations the design implies. Omit if none.
8. Changelog — Always present. Format: | Date | Change | Ref |

## Explicitly DO NOT include
- A System Context / C4Context / sequence / ER diagram section — diagrams live in diagrams/.
- Any code or code snippets — those live in examples/ only.
- Implementation plans, phasing, or execution sequencing — out of HLD scope (tracked in the issue/work tracker).

## Value Test — apply to every line; if NONE apply, remove it:
1. Bug prevention: Would an AI coder build worse against this design without it?
2. Decision quality: Would an AI coder make a worse architectural choice without it?
3. System understanding: Does it reveal a boundary/constraint not in README, ladrs/, nfrs/, or diagrams/?

## Anti-Patterns (MUST remove)
- Empty sections with "N/A"/"None" — omit entirely.
- (src: path) annotations or file listings as "components".
- Restating the README's intent/goals — AGENTS.md is guardrails, not narrative.
- Duplicating LADR prose or NFR targets — link/summarise instead.
- Generic boilerplate (e.g. "required env vars: ...").
- Business value / problem-solution-impact blocks (those belong in README.md).

## Drift Minimization
- If a design change alters documented behaviour/decisions, update AGENTS.md in the same change.
- Every update adds a Changelog row. Stale context is worse than no context.
EOF
