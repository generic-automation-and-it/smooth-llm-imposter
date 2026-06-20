# Diagrams — Responses Chat Tool History Normalization

The C1 context shows the narrow system boundary. The request-history flow is included because the
load-bearing behavior is an ordered validation/removal path that the C1 cannot show.

## System Context (C1)

SmoothLlmImposter sits between a Responses-mode OpenAI client and an OpenAI-compatible Chat
Completions upstream. This HLD affects only the routed OpenAI imposter path where the proxy downgrades
a Responses request to Chat Completions; the upstream's strict Chat validator is the dependency that
forces the history normalization.

```mermaid
C4Context
    title Responses Chat Tool History Normalization — System Context

    Person(client, "Responses-mode OpenAI client", "Codex or another client that sends OpenAI Responses requests with prior-turn history.")

    System(router, "SmoothLlmImposter", "Routes OpenAI requests and downgrades selected /responses calls to Chat Completions for configured imposters.")

    System_Ext(chatUpstream, "Strict Chat Completions upstream", "OpenAI-compatible provider that rejects invalid assistant tool-call history.")
    System_Ext(openAiDefault, "OpenAI Responses upstream", "Default or passthrough provider that already accepts Responses wire shape.")

    Rel(client, router, "Sends /responses requests", "HTTP/SSE")
    Rel(router, chatUpstream, "Sends downgraded Chat Completions requests with valid tool history", "HTTP/SSE")
    Rel(router, openAiDefault, "Relays off-path requests unchanged", "HTTP/SSE")
```

## Flow — Prior-Turn Tool History Downgrade

This flow earns its place because the design is about preserving only representable history while
removing invalid gaps before the strict upstream sees the Chat request.

```mermaid
flowchart TD
    A[Inbound OpenAI request] --> B{Matched imposter /responses to Chat downgrade?}
    B -->|no| C[Relay existing request transform path unchanged]
    B -->|yes| D[Read Responses input history in order]
    D --> E{Item is function_call?}
    E -->|yes| F{Matching function_call_output exists for call_id?}
    F -->|yes| G[Emit Chat assistant tool_calls message and adjacent tool response]
    F -->|no| H[Remove incomplete function_call history]
    E -->|no| I{Item is function_call_output?}
    I -->|yes| J{Already paired with emitted function_call?}
    J -->|yes| K[Do not emit duplicate output]
    J -->|no| L[Remove orphaned tool output]
    I -->|no| M[Convert non-tool item by existing Responses to Chat rules]
    G --> N[Continue preserving order]
    H --> N
    K --> N
    L --> N
    M --> N
    N --> O[Send Chat-valid request upstream]
```
