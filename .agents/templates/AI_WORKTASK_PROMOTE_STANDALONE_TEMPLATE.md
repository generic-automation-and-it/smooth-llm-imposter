# Task: [one-liner task description]

## Contexts
- [ ] list relevant context documents, domain files, or knowledge sources
- [ ] if no context document exists for this feature, add one here and include "create context document" in Instructions

## Instructions
- [ ] list specific requirements and acceptance criteria

## Constraints

### Context Loading (Phase 0 — MANDATORY FIRST)
- **Load domain/feature context BEFORE asking clarifying questions** — you cannot ask intelligent questions without understanding existing patterns
- Use the `load-context` skill with `[domain]` to find/create functional context, or rely on the `load-agents-context` PostToolUse hook which auto-injects ancestor `*_AGENTS.md` on first Read/Edit

### Model Selection Strategy

**Quality-first approach:** Opus for architectural decisions and gates, Sonnet for implementation and supporting work.

| Phase | Agent | Model | Rationale |
|-------|-------|-------|-----------|
| 0 | Orchestrator | **Opus** | Strategic context loading and decision gates |
| 1 | Orchestrator | **Opus** | Consolidating multiple specialist perspectives (reasoning-heavy) |
| 1 | PO Specialist | Sonnet | Domain knowledge, straightforward business clarifications |
| 1 | **Architect Specialist** | **Opus** | Technical feasibility, trade-offs, constraints (complex reasoning) |
| 1 | QA Specialist | Sonnet | Test scope assessment is domain-specific, not reasoning-heavy |
| 2 | Architect | Sonnet | Pattern analysis, existing code review (clear scope) |
| 3 | **Architect** | **Opus** | Architectural specification creation (foundational decisions) |
| 4 | **Architect** | **Opus** | Overall strategy and module breakdown (reasoning-heavy) |
| 4 | Backend/Frontend engineers | Sonnet | Module-specific plans (scope already defined by Architect) |
| 5 | Architect | Sonnet | AGENTS.md documentation (mechanical update) |
| 6 | Backend/Frontend engineers | Sonnet | Focused implementation (spec is clear, execution is mechanical) |
| 7 | **Architect** | **Opus** | Spec/implementation sync (architectural decisions) |
| 7 | Engineers | Sonnet | Code quality, patterns, security review |
| 7 | QA | Sonnet | Test coverage, acceptance criteria validation |
| 8 | Orchestrator | **Opus** | Final integration decision, changelog synthesis, commit strategy |

**Cost/Quality Trade-off:** ~40% Sonnet (implementation phases), ~60% Opus (architectural and gate phases) = higher quality decisions with reasonable cost.

---

### Git Behavior
> **Override:** This template explicitly allows commits during execution (Phase 6). This overrides the default repo policy (`git-policy.instructions.md`: "never commit unless explicitly asked") because the user grants commit permission by promoting a task with this template.
- **Commits: ALLOWED** — commit in logical chunks with clear messages
- **Push: NOT ALLOWED** — manual review required before push

### Phase Output Rules (MANDATORY — no exceptions)

1. **Label every phase** — Output `## Phase N: Name` as a visible header before executing each phase
2. **Label every skip** — If skipping a phase, output: `## Phase N: Name — Skipped: [one-line reason]`
3. **Sequential execution** — Phases MUST execute in declared order. Never reorder, combine, or nest (e.g., doing Phase 5 work inside Phase 6 is a violation)
4. **No silent phases** — Every phase in your chosen path MUST appear in output. If the user can't see it, it didn't happen

### Execution Phases

Follow in order. **Do not skip phases without outputting the skip reason. Do not proceed without explicit user confirmation at gates.**

| Phase | Name | Gate? | Purpose | Agents |
|-------|------|-------|---------|--------|
| 0 | Context Load | 🛑 MANDATORY | Read documents from **Contexts** section above | Orchestrator |
| 1 | Odin (Clarify) | 🛑 GATE | Consolidate domain/technical/test clarifications | Orchestrator + PO + Architect + QA (parallel) |
| 2 | Thoth (Analyze) | | Analyze tech stack, determine tech requirements, identify patterns | Architect |
| 3 | Forseti (Specify) | | Create technical specification with architectural decisions | Architect |
| 4 | Tyr (Plan) | 🛑 GATE | Present implementation plan + module breakdown | Architect → Backend/Frontend engineers (parallel) |
| 5 | Frigg (Document) | | Update AGENTS.md with approved plan under `## Requirements` | Architect |
| 6 | Thor (Execute) | | 🔨 YOLO MODE — implement autonomously on shared branch | Backend/Frontend engineers (parallel) |
| 7 | Heimdall (Review) | | Quality gate — spec/implementation sync check | Architect + Backend/Frontend engineers + QA (parallel) |
| 8 | Bragi (Record) | 🛑 MANDATORY | Document actual implementation, commit all work | Orchestrator |

**Lightweight path** (trivial tasks): Phase 0 → 1 → 6 → 7 → 8
Use when: single file, no architectural decisions, clear requirements.

**Full path** (non-trivial tasks): Phase 0 → 1 → 2 → 3 → 4 → 5 → 6 → 7 → 8
Use when: 3+ files, new patterns, cross-cutting concerns, or ambiguous scope.

### Agent Fleet Autonomy

> **The AI Coding Agent decides the optimal execution flow and agent fleet size — not just the phases listed above.**

The table above defines the *minimum* agent structure. The Orchestrator may spin up **additional parallel specialist agents** at any point in the workflow when it determines this will improve quality, speed, or coverage. Examples:

- Spawning a dedicated **security review agent** in parallel with QA during Phase 7
- Running **multiple backend engineers in parallel** across independent modules in Phase 6
- Launching a **documentation agent** alongside implementation agents in Phase 6
- Adding a **migration/rollback planning agent** in Phase 3 when schema changes are detected

**The agent has full autonomy to scale the fleet up or down based on task complexity.** Agents listed in the Phases table are a guide, not a ceiling. The Orchestrator should always prefer more parallelism when tasks are independent and model cost is acceptable.

---

## Phase 1: Odin (Clarify) — Orchestrator Multi-Agent Pattern

### Sub-Agent Invocation

**Orchestrator launches three sub-agents in parallel using Agent tool:**

```
Agent(
  description: "PO specialist clarification phase",
  subagent_type: "general-purpose",
  prompt: "[PO role instruction] + [worktask Contexts + Instructions] + [domain AGENTS.md files]",
  run_in_background: true,
  model: "sonnet"
)

Agent(
  description: "Architect specialist clarification phase",
  subagent_type: "general-purpose",
  prompt: "[Architect role instruction] + [worktask Contexts + Instructions] + [domain AGENTS.md files]",
  run_in_background: true,
  model: "opus"  # Higher reasoning for technical feasibility
)

Agent(
  description: "QA specialist clarification phase",
  subagent_type: "general-purpose",
  prompt: "[QA role instruction] + [worktask Contexts + Instructions] + [domain AGENTS.md files]",
  run_in_background: true,
  model: "sonnet"
)
```

**All three agents run in parallel. Orchestrator waits for all to complete before consolidating.**

### Sub-Agent Context & Responsibilities

Each agent receives:
- **Role Instruction** — specific focus area (see below)
- **Full worktask** — Contexts and Instructions sections
- **Relevant AGENTS.md files** — all domain context from Contexts checklist
- **Conversation history** — original user request and any clarifications

**PO Specialist (Sonnet):**
- Business intent and value proposition
- Acceptance criteria and success metrics
- User stories and edge cases
- Scope boundaries (what's IN, what's OUT)
- Related business context or dependencies

**Architect Specialist (Opus):**
- Technical feasibility and constraints
- System integration points and dependencies
- Performance, scalability, or security implications
- Trade-offs between proposed approaches
- Impact on existing architecture

**QA Specialist (Sonnet):**
- Testing scope and coverage expectations
- Acceptance criteria validation (are they testable?)
- Test tiers required (unit, integration, E2E)
- Known edge cases or failure scenarios to cover
- Regression test impact

### Sub-Agent Output Format

Each agent returns structured output:

```markdown
### [Role] Clarifications

- **Clarification 1**: [finding and question/recommendation]
- **Clarification 2**: [finding and question/recommendation]
- **Assumption validated**: [what was already clear, no question needed]
```

### Consolidation & Gate

**Orchestrator consolidates into unified response:**

```markdown
## Phase 1: Odin (Clarify) — Consolidated Findings

### Business Clarifications (PO Specialist)
- [PO findings]

### Technical Clarifications (Architect Specialist)
- [Architect findings]

### Test Scope Clarifications (QA Specialist)
- [QA findings]

---

**Gate Question:**
Does this consolidated understanding capture your intent? 
Reply 'approved' to proceed to Phase 2, or provide corrections.
```

**Proceed to Phase 2 only after user approval.**

---

## Phase 2: Thoth (Analyze) — Architect Only (Sonnet)

**Prerequisite:** Phase 1 consolidated clarifications approved by user.

**Architect responsibilities:**

1. **Determine technology stack requirements** — This output directly informs Phase 4 (Tyr) sub-agent selection:
   - Backend required? (Database changes, API endpoints, server-side logic, microservices integration, message queues)
   - Frontend required? (UI changes, TypeScript/React, CSS, state management, client-side validation)
   - Infrastructure/DevOps required? (Deployment, configuration, CI/CD, security)
   - **Output**: Clear YES/NO for each, with justification

2. **Analyze existing patterns and conventions**:
   - Similar features already implemented — what patterns do they follow?
   - Existing architecture decisions (ADRs) that apply to this task
   - Code organization, naming conventions, testing patterns
   - Technology stack baseline (what's already in use)

3. **Identify architectural constraints and dependencies**:
   - How does this task integrate with existing systems?
   - Breaking changes or migration paths needed?
   - Performance or scalability constraints
   - Security/compliance considerations

**Output format for Orchestrator:**

```markdown
## Phase 2: Thoth (Analyze)

### Technology Stack Requirements
- **Backend**: YES — [justification: DB changes, API endpoints, etc.]
- **Frontend**: NO — [justification: no UI changes]
- **Infrastructure**: NO — [justification: no deployment changes]

### Existing Patterns & Conventions
- Similar features: [list and reference]
- Relevant ADRs: [which decisions from code-review-standards.instructions.md apply]
- Code organization: [where should new code go]
- Testing patterns: [what test structure to follow]

### Architectural Constraints
- Integration points: [where this connects]
- Performance considerations: [if any]
- Security implications: [if any]
- Migration/breaking changes: [if any]

**Ready for Phase 3.**
```

**Gate**: Proceed to Phase 3 only if tech stack requirements are clear.

---

## Phase 3: Forseti (Specify) — Architect Only (Opus)

**Prerequisite:** Phase 2 analysis complete (tech stack determined).

**Architect responsibilities:**

1. **Create technical specification** with architectural decisions:
   - Architecture diagram or data model changes (if applicable)
   - API contracts, database schema changes (if backend)
   - UI/component structure (if frontend)
   - Error handling and logging strategy (following backend-logging-conventions.instructions.md)

2. **Document architectural decisions** — Use LADR format:
   - Decision title
   - Context (why this decision is needed)
   - Decision (what was chosen)
   - Consequences (what changes as a result)
   - Reference to code-review-standards.instructions.md ADRs if applicable

3. **Define testing approach**:
   - Test tiers and coverage expectations
   - Edge cases and failure scenarios
   - Integration test requirements (if applicable)

4. **Specify constraints and non-functional requirements**:
   - Performance targets
   - Security requirements
   - Scalability considerations
   - Compliance/audit trail needs

**Output format for Orchestrator:**

```markdown
## Phase 3: Forseti (Specify)

### Technical Specification

[Include architecture diagrams, data model, API contracts, etc.]

### Architectural Decisions

**LADR-XXX: [Decision Title]**
- Context: [why this decision]
- Decision: [what was chosen]
- Consequences: [what changes]
- Related ADRs: [links to code-review-standards.instructions.md decisions]

[Additional LADRs if needed]

### Testing Approach
- Test tiers: [unit/integration/E2E requirements]
- Coverage targets: [e.g., >80% for critical paths]
- Edge cases: [high-risk scenarios identified in Odin]

### Non-Functional Requirements
- Performance: [targets or constraints]
- Security: [requirements from clarifications]
- Scalability: [expectations for growth]

**Ready for Phase 4.**
```

**Gate**: Proceed to Phase 4 only if specification is complete and aligns with Phase 1 clarifications.

---

## Phase 4: Tyr (Plan) — Architect (Opus) → Backend/Frontend Engineers (Sonnet)

**Prerequisite:** Phase 3 specification approved by Architect and user.

**Orchestrator coordinates sequence:**

1. **Architect creates overall implementation plan** (Opus):
   - Ordered list of implementation steps
   - Module/component breakdown
   - File-level changes and dependencies
   - Risk mitigation strategies
   - Estimated scope (files touched, effort)

2. **Architect gates and awaits user approval**

3. **If backend needed** (from Phase 2 analysis): **Launch Backend Engineer** (Sonnet) in parallel with Frontend
   - Receives: Phase 3 spec + overall plan
   - Creates backend-specific plan: models, API changes, database migrations, service integrations

4. **If frontend needed** (from Phase 2 analysis): **Launch Frontend Engineer** (Sonnet) in parallel with Backend
   - Receives: Phase 3 spec + overall plan
   - Creates frontend-specific plan: components, state management, API contracts, styling approach

5. **Consolidate plans**:
   - Architect reviews backend/frontend plans for integration points and conflicts
   - If conflicts detected: Architect adjudicates
   - Present consolidated plan to user

**Output format:**

```markdown
## Phase 4: Tyr (Plan)

### Overall Implementation Strategy (Architect)
- Step 1: [implementation step] (File: X)
- Step 2: [implementation step] (File: Y)
- Dependencies: [what must complete before what]
- Risks: [identified risks and mitigation]

### Backend Plan (if needed)
- Database migrations: [schema changes]
- API endpoints: [new/modified endpoints]
- Service integrations: [how backend connects to systems]
- Dependencies: [what backend depends on]

### Frontend Plan (if needed)
- Components: [new/modified components]
- State management: [Redux/Context changes]
- API contracts: [what backend endpoints are expected]
- Dependencies: [what frontend depends on]

### Integration Points
- [How backend and frontend will interact]
- [Conflict resolution if any]

**Gate Question:**
Does this plan look correct? Reply 'approved' to proceed, or provide feedback.
```

**Gate**: Wait for user approval before proceeding to Phase 5.

---

## Phase 5: Frigg (Document) — Architect Only (Sonnet)

**Prerequisite:** Phase 4 plan approved by user.

**Architect responsibilities:**

1. **Update domain AGENTS.md** (or create if missing):
   - Add to `## Requirements` section: accepted plan from Phase 4
   - Document architectural decisions (from Phase 3 LADRs)
   - Record tech stack requirements and integration points
   - Add test references (L0/L1, test sub-folder paths)

2. **Update or create project ADRs** (`.docs/adrs/`):
   - For each Phase 3 LADR: create corresponding ADR file if it's a foundational architectural decision
   - Update existing ADRs if implementation changes behavior they document

3. **Update root AGENTS.md** if needed:
   - Cross-reference new feature/domain context document
   - Note significant architectural changes

4. **Update changelog** in context documents:
   - Record what was changed and why (for future reference)

**Output format:**

```markdown
## Phase 5: Frigg (Document)

### Context Documentation Updated
- [Domain]_AGENTS.md: [sections updated]
- Root AGENTS.md: [sections updated]

### Architectural Decisions Recorded
- LADR-XXX: [ADR file created or updated]
- LADR-YYY: [ADR file created or updated]

### Changelog Entries
- [Date] | [Change] | [Reference to this task]

**Ready for Phase 6.**
```

---

## Phase 6: Thor (Execute) — Backend/Frontend Engineers (Sonnet) in Parallel

**Prerequisite:** Phase 5 documentation complete.

**Execution model:**

1. **If both backend AND frontend needed**:
   - Launch Backend Engineer (Sonnet) and Frontend Engineer (Sonnet) in parallel
   - Each executes independently on the **shared branch** (no individual commits yet)
   - Each follows Phase 6 YOLO MODE: implement, test, no TODOs
   - On failure: engineer attempts to fix forward. If blocked after 2 attempts, report to Orchestrator

2. **If backend only or frontend only**:
   - Launch single engineer (Sonnet)
   - Same execution model

3. **If neither** (rare — unlikely in Full path):
   - Skip Phase 6

**Output format:**

Each engineer returns:
```markdown
## Phase 6: Thor (Execute)

### Implementation Complete
- [File 1]: [what was implemented]
- [File 2]: [what was implemented]
- Tests: [all passing, coverage X%]

**Ready for Phase 7 (Review).**
```

**No user gate.** Engineers execute autonomously until both are done.

---

## Phase 7: Heimdall (Review) — Architect (Opus) + Engineers (Sonnet) + QA (Sonnet) in Parallel

**Prerequisite:** Phase 6 execution complete (all engineers done).

**Orchestrator launches three reviewers in parallel:**

1. **Architect (Opus) — Specification Sync**:
   - Does implementation match Phase 3 spec?
   - Are architectural decisions honored?
   - Any design issues?

2. **Backend Engineer (Sonnet) — Code Quality**:
   - Backend code follows standards (if backend work done)
   - Tests adequate and passing
   - No OWASP vulnerabilities

3. **Frontend Engineer (Sonnet) — Code Quality**:
   - Frontend code follows standards (if frontend work done)
   - Tests adequate and passing
   - No OWASP vulnerabilities

4. **QA Specialist (Sonnet) — Test Coverage**:
   - Test coverage adequate for acceptance criteria?
   - Edge cases covered?
   - Acceptance criteria met?

**Consolidation & Decision:**

Orchestrator consolidates reviews:
- If all pass: proceed to Phase 8
- If issues found: present to user with options:
  - **Option A**: Fix issues in a follow-up execution (re-run Phase 6 with feedback)
  - **Option B**: Accept known issues and document (update AGENTS.md)
  - **Option C**: Block and ask for clarification/requirements change

**Output format:**

```markdown
## Phase 7: Heimdall (Review)

### Architect Review (Spec Sync)
- ✅ Implementation matches Phase 3 spec
- ✅ Architectural decisions honored
- Issues (if any): [list with remediation]

### Code Quality Reviews
- Backend: ✅ standards met, tests passing
- Frontend: ✅ standards met, tests passing
- Issues (if any): [list with remediation]

### Test Coverage Review
- Acceptance criteria: ✅ covered
- Edge cases: ✅ covered
- Coverage %: [X%]

### Gate Decision
- ✅ Ready for Phase 8 (commit and record)
- OR ⚠️ Issues found — [ask user for remediation approach]
```

---

## Phase 8: Bragi (Record) — Orchestrator (Opus) Commits All Work

**Prerequisite:** Phase 7 passed (or user approved issues).

**Orchestrator responsibilities:**

1. **Commit all work in logical chunks**:
   - Backend changes (if applicable): 1 commit per feature/concern
   - Frontend changes (if applicable): 1 commit per feature/concern
   - Test additions: committed with corresponding feature
   - Documentation updates: separate commit
   - Format: conventional commits (feat, fix, docs, test, chore, etc.)
   - All commits include reference to worktask/ticket

2. **Update AGENTS.md context document**:
   - Record actual implementation details (not just planned)
   - Update Tech Stack section with what was actually used
   - Update Key Behaviors if implementation differs from spec
   - Add Changelog entry: date, change, reference

3. **Update or create ADRs**:
   - If Phase 3 LADRs are still accurate: mark as finalized
   - If implementation changed decisions: update ADR with actual outcome
   - Add implementation status and rationale

4. **Finalize changelog**:
   - Comprehensive entry describing what was implemented
   - Breaking changes (if any)
   - Migration instructions (if needed)
   - Reference to this task

**Output format:**

```markdown
## Phase 8: Bragi (Record)

### Commits Created
- [commit 1]: [conventional message with ticket reference]
- [commit 2]: [conventional message with ticket reference]
- [commit 3]: [conventional message with ticket reference]

### Documentation Updated
- [Domain]_AGENTS.md: [sections changed]
- ADR files: [updated with actual implementation details]
- Root AGENTS.md: [if applicable]

### Changelog Entry
[Comprehensive entry for release notes]

### Task Complete ✅
Branch is ready for PR review. All work committed to [branch name].
```

**No user gate.** Orchestrator commits autonomously after Phase 7 passes.

---

> AI loading note: Skip this section during routine task execution. Use it only when updating this template.

| Date | Task | Changes |
|------|------|---------|
| 2026-05-30 | | Initial version. |
