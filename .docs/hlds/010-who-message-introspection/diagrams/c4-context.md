# Diagrams — Who-Message Introspection

## C1 — System Context

The probe does not introduce any new external system. The system context is identical to
HLD 001's; the short-circuit is internal to the proxy container. The diagram below
repeats C1 for completeness, with the probe's short-circuit noted as an internal
behavior of the proxy.

```mermaid
C4Context
    title SmoothLlmImposter — System Context (HLD 010 overlay)

    Person(agent, "Agent client", "Sends chat requests; may probe with `who?`.")
    Person(human, "Human client", "Sends chat requests via SDK.")

    System(proxy, "SmoothLlmImposter", "Stateless same-dialect router. Internally\nshort-circuits `who?` probes (HLD 010)\nwithout calling an upstream.")

    System_Ext(openai, "OpenAI-dialect upstreams", "ChatGPT, opencode-go, OpenRouter, …")
    System_Ext(anthropic, "Anthropic-dialect upstreams", "Anthropic API, opencode-go Anthropic, …")

    Rel(agent, proxy, "POST /openai/… or /anthropic/…")
    Rel(human, proxy, "POST /openai/… or /anthropic/…")
    Rel(proxy, openai, "forward (skipped on `who?` match)")
    Rel(proxy, anthropic, "forward (skipped on `who?` match)")
```

## Sequence — probe match vs. forward

Two scenarios on the same diagram. The `alt` block shows the short-circuit.

```mermaid
sequenceDiagram
    autonumber
    participant C as Client
    participant H as Host (RoutingEndpoints)
    participant R as IImposterRouter
    participant W as IWhoMessageResponder
    participant F as IUpstreamForwarder
    participant U as Upstream

    C->>H: POST /openai/v1/chat/completions<br/>{messages:[…,{role:user,content:X}], stream:Y}
    H->>R: PlanAsync(dialect, body, callerHeaders)
    R-->>H: RoutePlan (decision, inboundModel, authScheme)

    alt X == "who?" AND Y == false AND feature enabled
        H->>W: TryBuildResponse(dialect, body, plan)
        W-->>H: synthetic envelope (200)
        H-->>C: 200 synthetic reply (no forwarder call)
    else no match, OR streaming, OR feature disabled
        H->>F: SendAsync(plan, …)
        F->>U: HTTP request
        U-->>F: upstream response
        F-->>H: HttpResponseMessage
        H-->>C: stream / reply (byte-identical to pre-HLD)
    end
```

## Flowchart — decision gate

The responder's internal gate. Every "no" falls through to the forward path.

```mermaid
flowchart TD
    A[Request enters HandleAsync] --> B{feature enabled?}
    B -- no --> Z[Forward path]
    B -- yes --> C{body has stream:true?}
    C -- yes --> Z
    C -- no --> D{parse last user message}
    D -- no messages / no user role --> Z
    D -- last user content is string or text[] --> E{concat content, trim}
    E --> F{== 'who?' ordinal?}
    F -- no --> Z
    F -- yes --> G[Build dialect envelope with<br/>Imposter/Passthrough content text]
    G --> H[Write 200 and return]
```

## Diagram selection rationale

Per `references/diagram-selection.md`:

- **C1 System Context** — included (mandatory floor). The probe adds no external
  dependency, so C1 is a re-statement of HLD 001's with a note.
- **Sequence** — included because the probe adds a new branch to the request flow;
  readers need to see where the short-circuit sits relative to the forwarder.
- **Flowchart** — included because the decision gate has 5 short-circuit predicates;
  a flowchart makes the "fail transparent" property visible at a glance.
- **Container / Component / ER / Class** — not included. The probe adds no new
  container, no new persistent entity, and no class hierarchy worth diagramming.
