# AGENTS.md Template Guide

This guide explains how to create and maintain nested `AGENTS.md` context files for AI assistants.

## Project Structure

```
├── src/
│   ├── [*].Application/
│   │   └── {Feature}/
│   │       └── AGENTS.md                     # Feature-specific context
│   │
│   └── [*].Infrastructure/
│       └── {Concern}/
│           └── AGENTS.md                     # Infrastructure context
│
├── [*].Domain/
│   └── Entities/
│       └── AGENTS.md                         # ERD and entity changes
│
├── .docs/
│   ├── adr/                                 # Architecture Decision Records
│   ├── nfr/                                 # Non-Functional Requirements
│   │   └── AGENTS.md                         # Security, performance, etc.
│   ├── TEMPLATE_AGENTS.md                   # Template for new AGENTS.md
│   └── TEMPLATE_README.md                   # This file
│
├── .github/
│   └── workflows/
│       └── {workflow}.AGENTS.md             # CI/CD workflow context
│
└── CLAUDE.md                                # Root project context
```

## Placement Rules

### Rule 1: Every PR Must Include AGENTS.md Changes
Each pull request must either:
- Create a new nested `AGENTS.md` file, OR
- Modify an existing `AGENTS.md` file

This ensures AI context stays synchronized with code changes.

### Rule 2: Non-Functional Concerns → `.docs/nfr/`
Place infrastructure and cross-cutting concerns in `.docs/nfr/{concern}/AGENTS.md`:
- Security configurations
- Performance requirements
- Logging/monitoring setup
- Authentication/authorization
- Caching strategies

### Rule 3: Functional Features → Application Folder
Place feature-specific context alongside the feature code.

### Rule 4: ERD/Domain Changes → Domain Folder
Place entity relationship and domain model changes under the domain layer.

## Naming Convention

Each feature/layer folder owns one `AGENTS.md` (Claude Code auto-discovers nested `AGENTS.md` files):
- ✅ `Features/Routing/AGENTS.md`
- ✅ `Features/Credentials/AGENTS.md`
- ❌ `Features/Routing/ROUTING_AGENTS.md`
- ❌ `Features/Routing/routing_agents.md`

## Template Sections Reference

| Section | Purpose | When to Update |
|---------|---------|----------------|
| TL;DR | One-liner for AI agents | Every major change |
| Non-Negotiables | Forbidden patterns | When anti-patterns discovered |
| System Context | Architecture diagrams | Architectural changes |
| Architecture Decisions | LADRs | Design decisions |
| Key Behaviors | Non-obvious behaviors | Feature changes |
| Test References | Test tier and path | When tests added/modified |
| Quality Constraints | Feature-specific NFRs | When constraints change |
| Migration Plans | Planned changes | When deprecations planned |
| Changelog | Change history | Every PR |

## Validation

The Gemini CLI Code Review workflow validates AGENTS.md requirements:
- **FULL reviews**: Must include new/modified `AGENTS.md` file
- **Naming**: Must be `AGENTS.md` in the closest feature/layer folder
- **Template sections**: New files must include required sections

See `.agents/rules/knowledge-conventional-contexts-quality.instructions.md` for full standards.
