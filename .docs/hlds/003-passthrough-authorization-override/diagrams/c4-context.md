# Diagrams — Passthrough Authorization Override

Three diagrams, each earning its place:

- **System Context (C1)** — mandatory floor; shows the new operator control surface alongside the existing actors.
- **Flow** — *the* diagram to read: where the override gate sits on the passthrough branch and that imposter is untouched.
- **Sequence** — the toggle is a side-effecting interaction (in-memory state change) with a conditional `403`, plus the forced-`Bearer` forward; worth one ordered view.

No ER or class diagram: this design adds **no** entity or type to HLD 002's data model, so a data/domain diagram would only restate HLD 002.

## System Context (C1)

The router exposes key-less proxy endpoints to LLM callers and a separate admin-authorized control surface to
an operator. This HLD adds one operator control — the per-dialect authorization override — that influences how
the router presents credentials to upstreams on the **passthrough** path only. Stored credentials live in
PostgreSQL (HLD 002); the override switch itself is in-memory.

```mermaid
C4Context
    title Passthrough Authorization Override — System Context

    Person(operator, "Operator", "Flips the per-dialect override via curl (admin key).")
    Person(caller, "LLM Caller", "Claude Code / OpenAI SDK sending dialect requests.")

    System(router, "SmoothLlmImposter Router", "Same-dialect router: imposter rewrite or passthrough; streams responses.")

    SystemDb_Ext(store, "Credential Store (PostgreSQL)", "Encrypted stored credentials (HLD 002). Active credential per dialect.")
    System_Ext(openai, "OpenAI-dialect upstream", "Receives passthrough/imposter requests.")
    System_Ext(anthropic, "Anthropic-dialect upstream", "Receives passthrough/imposter requests.")

    Rel(operator, router, "PUT/DELETE/GET /routing/{dialect}/{provider}/override-authorization", "HTTPS + X-Admin-Api-Key")
    Rel(caller, router, "POST /v1/* dialect requests", "HTTPS")
    Rel(router, store, "Reads active credential (passthrough only)", "EF Core")
    Rel(router, openai, "Forwards (Bearer when override ON, else config/stored scheme)", "HTTPS")
    Rel(router, anthropic, "Forwards (Bearer when override ON, else config/stored scheme)", "HTTPS")
```

## Flow — Override-gated passthrough

Extends HLD 002's credential-resolution flow with one new gate on the **passthrough** branch. The
matched-imposter branch is identical to HLD 001/002 and never sees the switch.

```mermaid
flowchart TD
    A[Inbound dialect request] --> B{First imposter mapping matches?}
    B -->|yes| C[Imposter path: rewrite model, inject caching,<br/>apply CONFIG key. Switch NOT consulted.]
    B -->|no| D{Dialect has a default/passthrough provider?}
    D -->|no| E[404 dialect-shaped error]
    D -->|yes| F{Override ON for this dialect?}
    F -->|no| G[HLD 002 passthrough:<br/>active credential with its own scheme,<br/>else config key]
    F -->|yes| H{Active stored credential exists?}
    H -->|no| I[Fail closed:<br/>dialect-shaped auth error<br/>NEVER x-api-key/config fallback]
    H -->|yes| J[Decrypt secret just-in-time<br/>send Authorization: Bearer<br/>OMIT x-api-key]
    C --> Z[Stream response back]
    G --> Z
    J --> Z
```

## Sequence — Enable then forward

The toggle mutates in-memory state and may refuse with `403`; a subsequent passthrough request is forwarded
with a forced `Bearer` header. Participants are roles, not classes.

```mermaid
sequenceDiagram
    participant Op as Operator (curl)
    participant Ctrl as Override Control (admin-authed)
    participant Sw as Override Switch (in-memory)
    participant St as Credential Store
    participant Rt as Passthrough Forwarder
    participant Up as Upstream

    Op->>Ctrl: PUT /routing/anthropic/override-authorization (X-Admin-Api-Key)
    Ctrl->>St: Active credential for anthropic?
    alt no active credential
        St-->>Ctrl: none
        Ctrl-->>Op: 403 (cannot arm; switch stays OFF)
    else active credential exists
        St-->>Ctrl: present
        Ctrl->>Sw: set anthropic = ON
        Ctrl-->>Op: 200 (override ON)
    end

    Note over Op,Up: later — a passthrough (no-imposter-match) request arrives
    Rt->>Sw: override ON for anthropic?
    Sw-->>Rt: ON
    Rt->>St: active credential (decrypt secret)
    Rt->>Up: forward with Authorization: Bearer, no x-api-key
    Up-->>Rt: streamed response
```
