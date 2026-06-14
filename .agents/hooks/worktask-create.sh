#!/bin/bash
# Hook: Create a work task file from the standalone template when user requests it
# Event: UserPromptSubmit
# Triggers: "create worktask", "create work task", "create work-task",
#           "make worktask", "make work task", "make work-task" (case insensitive)

PROMPT=$(jq -r '.prompt // empty')
[ -z "$PROMPT" ] && exit 0

if echo "$PROMPT" | grep -qiE '(create|make)[[:space:]]+(a[[:space:]]+)?work[[:space:]-]?task'; then
  # Ensure the work-tasks directory exists
  mkdir -p .context/work-tasks

  cat <<'EOF'
WORK TASK CREATION TRIGGERED — Follow these steps:

1. **Understand the task**: Parse the user's prompt and conversation context to determine what work is being requested.

2. **Read the template**: Load `.agents/templates/AI_WORKTASK_PROMOTE_STANDALONE_TEMPLATE.md` as the base structure.

3. **Populate Contexts section** — List ALL resources needed for a standalone AI coder:
   ✓ Domain/feature AGENTS.md files (e.g., HOST_AGENTS.md, feature area docs)
   ✓ Architecture Decision Records (ADRs) from `.agents/rules/code-review-standards.instructions.md` relevant to the change
   ✓ Non-Functional Requirements (NFRs) that apply to this work
   ✓ Related rule files: backend-logging-conventions.instructions.md, git-policy.instructions.md, AI_WORKTASK_PROMOTE_TEMPLATE.md, etc.
   ✓ Root AGENTS.md and project overview for system context
   ✓ Existing similar features/implementations to reference
   ✓ Database schema (if database changes expected)
   ✓ API documentation (if API changes expected)
   ✓ UI/UX specifications (if frontend changes expected)
   Note: If no feature AGENTS.md exists, include "create [domain]_AGENTS.md" in Instructions

4. **Populate Instructions section** — Capture complete requirements:

   **Phase 1 (Odin) clarifications** (PO/Architect/QA will validate):
   ✓ Task summary: what is being built/fixed and why
   ✓ Business intent: problem statement, value proposition, success metrics
   ✓ Acceptance criteria: specific, measurable conditions for success
   ✓ Requirements: functional requirements, edge cases, constraints
   ✓ Scope boundaries: what IS included, what IS NOT included
   ✓ Related business context or dependencies (for PO clarity)

   **Phase 2 (Thoth) analysis** (Architect will determine):
   ✓ Integration points: where this connects to existing systems (APIs, databases, services)
   ✓ Performance expectations: latency, throughput, scalability targets (if any)
   ✓ Security requirements: authentication, authorization, data protection needs
   ✓ Compliance: regulatory, audit trail, or logging requirements

   **Phase 3 (Forseti) specification** (Architect will create):
   ✓ Data model: if database changes, outline the schema changes
   ✓ API contracts: if backend changes, list new/modified endpoints or service calls
   ✓ UI/UX specification: if frontend changes, describe UI structure or wireframes

   **Phase 4-6 (Tyr/Thor) execution**:
   ✓ Testing expectations: what tests are required (unit, integration, E2E)
   ✓ Test tiers required: L0 (unit), L1 (integration), L2 (E2E) expectations

   **Phase 7 (Heimdall) review gates**:
   ✓ Non-functional requirements: performance, security, compliance, scalability constraints
   ✓ Code quality standards: what patterns to follow (from code-review-standards.instructions.md)

   **Phase 8 (Bragi) documentation**:
   ✓ Dependencies: other work that must complete first, or code this depends on
   ✓ Related PRs/issues: link to Linear tickets, GitHub issues, or related work
   ✓ Examples or references: point to similar code patterns to follow
   ✓ Known gotchas or pitfalls: issues discovered during investigation that the coder should avoid
   ✓ Migration path: if breaking changes, how to migrate existing data/code

5. **Choose a filename**: Use kebab-case matching the task summary, e.g. `add-vessel-eta-validation.md` or `fix-claims-filter-bug.md`

6. **Write the file** to `.context/work-tasks/<filename>.md` with:
   - Title: concise one-liner
   - Contexts: complete checklist of relevant documents
   - Instructions: fully populated with all items from step 4

7. **Quality check** before confirming:
   - Would a new AI coder understand the full scope without asking questions?
   - Are all necessary context files listed?
   - Are acceptance criteria testable and measurable?
   - Are constraints and scope boundaries explicit?
   - Are dependencies and integration points clear?

8. **Confirm** to the user: show the file path and a summary of what was captured, including:
   - Key acceptance criteria
   - Files/domains affected
   - Estimated complexity

9. **Recommend orchestrator model**: Evaluate task complexity and workflow:
   - **Single-agent (Sonnet)** — Config changes, single-file fixes, straightforward CRUD, <5 files, clear patterns. Executes lightweight path (0→1→6→7→8)
   - **Orchestrated (Opus orchestrator)** — Cross-cutting concerns, architectural decisions, ambiguous requirements, 5+ files, new patterns, complex integrations. Executes full path (0→1→2→3→4→5→6→7→8) with multi-agent sub-teams (PO/Architect/QA in Phase 1, Backend/Frontend engineers in Phase 4-6)
   - Present with one-line rationale and workflow

IMPORTANT: The work task file is a filled-in copy of the template — populate with REAL content from investigation, not generic templates. An AI coder should be able to execute this task with only the information in this file and the linked context documents.
EOF
fi

exit 0
