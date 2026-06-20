# Diagrams — OpenAI /models Endpoint Aggregation

The **C1 System Context** below is the mandatory floor for every HLD. The single supporting diagram
is a **request-decision flow**: this HLD introduces a new local-answer fork into an otherwise
transparent proxy, and that fork is the load-bearing idea worth showing end-to-end.

## System Context (C1)

An LLM client (e.g. Codex) points its OpenAI base URL at the router. For `GET /openai/v1/models`
the router answers **from its own configuration** — it does not call an upstream. All other OpenAI
traffic continues to forward to the configured upstream providers as before.

```mermaid
C4Context
    title OpenAI /models Endpoint Aggregation — System Context

    Person(client, "LLM Client (e.g. Codex)", "Calls GET /openai/v1/models to discover models.")

    System(router, "SmoothLlmImposter Router", "Stateless same-dialect router. Answers /models from configured route catalogue; forwards everything else.")

    System_Ext(upstreams, "OpenAI-dialect Upstreams", "Real / imposter providers the router forwards completion traffic to.")

    Rel(client, router, "GET /openai/v1/models", "HTTPS")
    Rel(client, router, "Completion requests (forwarded)", "HTTPS")
    Rel(router, upstreams, "Forwards completions only — NOT /models", "HTTPS")
```

## Flow — `GET /openai/v1/models` decision

The handler recognizes exactly one case (OpenAI dialect + `GET` + post-prefix path `/v1/models`,
per LADR-03) and answers it locally; every other request falls through to the existing routing path
unchanged.

```mermaid
flowchart TD
    A[Inbound request] --> B{OpenAI dialect AND GET AND path is /v1/models?}
    B -->|no| C[Existing routing path]
    C --> C1{Has JSON body?}
    C1 -->|yes| C2[Plan: match imposter or default, transform]
    C1 -->|no| C3[Plan passthrough to dialect default]
    C2 --> C4[Forward upstream, stream response]
    C3 --> C4

    B -->|yes| D[Collect 'to' from all OpenAI routes]
    D --> E[Exclude providers with no mappings]
    E --> F[Distinct / dedup the 'to' set]
    F --> G[Build OpenAI ListModelsResponse: object=list, data of Model objects]
    G --> H[200 application/json — no upstream call, no DB]
```
