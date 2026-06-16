# Diagram Selection — investigate, then recommend

The C1 System Context is the **mandatory floor**. Everything else is earned: investigate the
design, then recommend the smallest set of diagrams that makes the architecture clear. Do not
add a diagram by default — add it because it answers a question the C1 cannot.

Surface recommendations to the user **before** writing them, each with a one-line rationale.

## The floor

| Diagram | Mermaid | Always include |
|---|---|---|
| System Context (C1) | `C4Context` | Yes — every HLD. System + immediate external actors/dependencies, with `Rel()` protocols. No internals. |

## Recommend when the design warrants it

| Diagram | Mermaid | Recommend when… |
|---|---|---|
| Container view (C2) | `C4Container` | The system is >1 deployable/runtime unit, or the split between units is a load-bearing decision. |
| Flow | `flowchart TD` / `graph TB` | There is a process or decision path worth showing end-to-end (routing, branching, state transitions). This is the diagram people actually read. |
| Sequence | `sequenceDiagram` | An interaction has 3+ steps **or** any side effect (email, queue message, external API call, push notification). Participants are roles, not class names. 5–10 steps max. |
| Data model | `erDiagram` | 3+ related entities with non-obvious relationships. Skip for single-entity or obvious-named relations. |
| Domain types | `classDiagram` | Key types/contracts and their relationships drive the design and aren't obvious from the data model. |

## Rules

- **Each diagram earns its place.** If it restates the C1 or the prose, drop it.
- **One concern per diagram.** A sequence diagram shows one flow; add another file for another flow.
- **No code.** Diagrams describe structure and interaction, not implementation. Code samples go in `examples/`.
- **Roles over names.** Use logical participant/container names ("Handler", "Gateway"), not concrete class or file names — the design predates the code.
- **Render-clean.** Verify Mermaid parses (no syntax errors) before marking the HLD ready.
- Multiple diagrams may live as sections in `diagrams/c4-context.md`, or as separate files in `diagrams/` (e.g. `sequence-checkout.md`) when they grow large.
