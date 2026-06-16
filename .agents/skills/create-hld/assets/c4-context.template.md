# Diagrams — {{TITLE}}

The **C1 System Context** below is the mandatory floor for every HLD. Add further diagrams
only where they earn their place (see the skill's `references/diagram-selection.md`); each
addition gets its own `## ` section here (or its own file in this folder) and a one-line
rationale.

## System Context (C1)

<2–4 sentences: what this system is and its immediate external dependencies.>

```mermaid
C4Context
    title {{TITLE}} — System Context

    Person(user, "User / Actor", "Who initiates the primary flow.")

    System(thisSystem, "{{TITLE}}", "What this component does, in one line.")

    System_Ext(ext1, "External System", "A dependency this system talks to.")

    Rel(user, thisSystem, "Uses", "HTTPS")
    Rel(thisSystem, ext1, "Calls", "Protocol")
```

<!--
Recommended additional diagrams — keep only those that add understanding, delete the rest:

## Container View (C2)        — when the system splits into >1 deployable/runtime unit
```mermaid
C4Container
    title {{TITLE}} — Containers
    ...
```

## Flow — <named flow>        — process/decision flows; a path through the system
```mermaid
flowchart TD
    A[Start] --> B{Decision}
    B -->|yes| C[Action]
    B -->|no| D[Alternative]
```

## Sequence — <named flow>    — interactions with 3+ steps OR side effects (email, queue, external call)
```mermaid
sequenceDiagram
    participant A as Caller
    participant B as Service
    A->>B: request
    B-->>A: response
```

## Data Model               — 3+ related entities with non-obvious relationships
```mermaid
erDiagram
    ENTITY_A ||--o{ ENTITY_B : has
```

## Domain Types             — key classes/types and their relationships
```mermaid
classDiagram
    class TypeA
    TypeA --> TypeB
```
-->
