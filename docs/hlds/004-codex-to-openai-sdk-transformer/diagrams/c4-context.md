# Diagrams — CodexToOpenAiSdk Transformer

The **C1 System Context** below is the mandatory floor for every HLD. The request-flow sequence is
added because the *placement* of the normalization step and the request-only boundary are the
load-bearing ideas of this design — a flow makes that boundary legible in a way prose cannot.

## System Context (C1)

A vanilla OpenAI-dialect agent client calls the router. On a matched OpenAI imposter route, the
router may normalize the request before forwarding it to a strict OpenAI-compatible upstream; the
upstream's response is streamed back untouched. Unmatched/passthrough traffic is forwarded
transparently to the real provider. The normalization seam is internal to the router (not shown at
C1).

```mermaid
C4Context
    title CodexToOpenAiSdk Transformer — System Context

    Person(client, "Vanilla agent client", "OpenAI-dialect client (e.g. Codex). Not modified per-upstream.")

    System(router, "SmoothLlmImposter router", "Stateless same-dialect router; opt-in request normalization on matched OpenAI imposter routes.")

    System_Ext(strict, "Strict OpenAI-compatible upstream", "Imposter target with tighter request validation than OpenAI.")
    System_Ext(real, "Real provider", "Default/passthrough upstream for unmatched models.")

    Rel(client, router, "Sends OpenAI-dialect request", "HTTPS / SSE")
    Rel(router, strict, "Forwards normalized request, streams response back", "HTTPS / SSE")
    Rel(router, real, "Forwards transparently (no normalization)", "HTTPS / SSE")
```

## Request flow — normalize in, relay out

Shows where normalization sits and the boundary: tool normalization reshapes the request before
forwarding. The response is relayed **unchanged on every route except the LADR-05 downgrade bridge**,
where a `/responses` request was downgraded to Chat and the Chat response stream is translated back to
Responses events (incrementally, never buffered).

```mermaid
sequenceDiagram
    participant C as Vanilla client
    participant R as Router (Application)
    participant N as Normalization seam
    participant T as Chat→Responses translator (LADR-05)
    participant U as Strict upstream

    C->>R: OpenAI-dialect request
    Note over R: Resolve route (dialect + model)
    alt Matched OpenAI imposter route AND provider opted in
        R->>N: Request body (after model/caching/Responses→Chat transform)
        N-->>R: Normalized request body
    else Passthrough / default / opt-out
        Note over R: No normalization (byte-transparent)
    end
    R->>U: Forward request
    U-->>R: Response stream (SSE)
    alt Downgraded /responses→Chat (LADR-05)
        R->>T: Chat Completions SSE (line by line)
        T-->>C: Responses events (incremental, never buffered)
    else All other routes
        R-->>C: Relay response unchanged
    end
```
