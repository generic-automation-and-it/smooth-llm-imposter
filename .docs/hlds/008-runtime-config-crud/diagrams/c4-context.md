# Diagrams — Runtime Config CRUD & Provider-Addressable Credentials

The **C1 System Context** below is the mandatory floor. Two further diagrams earn their place: a **sequence**
showing how a runtime mutation becomes visible on the next proxied request (the crux of "on the fly"), and a
**container view** showing the two admin boundaries over one in-memory registry plus the optional credential
database.

## System Context (C1)

The router exposes OpenAI- and Anthropic-dialect proxy endpoints to SDK callers and an authenticated admin
surface to operators. Operators reshape routing and credentials at runtime; the registry is in-memory and
seeded from config + environment, with an optional database backend only for persisted credentials.

```mermaid
C4Context
    title Runtime Config CRUD and Provider-Addressable Credentials — System Context

    Person(operator, "Operator", "Manages routing config and credentials via the admin API.")
    Person(caller, "SDK Caller", "Claude Code / Codex / OpenAI or Anthropic SDK.")

    System(router, "SmoothLlmImposter Router", "Stateless dialect router with a runtime-mutable, in-memory provider registry.")

    System_Ext(openaiUpstream, "OpenAI-dialect Upstream", "Real or imposter OpenAI-compatible provider.")
    System_Ext(anthropicUpstream, "Anthropic-dialect Upstream", "Real or imposter Anthropic-compatible provider.")
    System_Ext(credDb, "Credential Database (optional)", "PostgreSQL — opt-in encrypted credential persistence.")

    Rel(operator, router, "CRUD providers / credentials / overrides", "HTTPS + X-Admin-Api-Key")
    Rel(caller, router, "Proxied LLM requests", "HTTPS")
    Rel(router, openaiUpstream, "Forwards (rewritten)", "HTTPS")
    Rel(router, anthropicUpstream, "Forwards (rewritten)", "HTTPS")
    Rel(router, credDb, "Reads/writes credentials when configured", "EF Core / Npgsql")
```

## Sequence — Runtime mutation becomes visible

A successful admin write mutates the in-memory registry; the next proxied request reads the current registry
through `IOptionsSnapshot` (re-evaluated per request scope) and resolves against the new configuration —
no restart, no cache-invalidation step.

```mermaid
sequenceDiagram
    participant Operator
    participant Admin as Admin API
    participant Registry as Runtime Registry (in-memory)
    participant Router as Routing Resolver (per request scope)
    participant Upstream

    Operator->>Admin: PUT /admin/providers/{key} (routing config)
    Admin->>Admin: Authorize + validate
    Admin->>Registry: Apply mutation
    Admin-->>Operator: 200 OK

    Note over Registry,Router: Next inbound request opens a new scope
    Router->>Registry: Read current providers (IOptionsSnapshot)
    Router->>Router: Resolve route (skip disabled)
    Router->>Upstream: Forward per new configuration
    Upstream-->>Router: Response
```

## Container View (C2)

Two admin boundaries write one registry: provider-config (secret-free) and credentials (secret-only). The
resolution path reads the registry per request; the optional database backs only the credential store.

```mermaid
C4Container
    title Runtime Config CRUD — Containers

    Person(operator, "Operator")
    Person(caller, "SDK Caller")

    System_Boundary(router, "SmoothLlmImposter Router") {
        Container(providerAdmin, "Provider-Config Admin", "Minimal API + Mediator", "CRUD routing config; secret-free.")
        Container(credAdmin, "Credential Admin", "Minimal API + Mediator", "CRUD secrets; provider-keyed.")
        Container(overrideAdmin, "Override Control", "Minimal API + Mediator", "Provider-addressable authorization override.")
        Container(registry, "Runtime Registry", "In-memory, IOptionsSnapshot", "Mutable provider config; seeded from config + env.")
        Container(resolver, "Routing Resolver", "Per-request scope", "Resolves dialect+model to a route; skips disabled.")
        Container(forwarder, "Upstream Forwarder", "IHttpClientFactory", "Streams the rewritten request upstream.")
        Container(credStore, "Credential Store", "In-memory default or EF Core opt-in", "Provider-keyed secrets.")
    }

    System_Ext(upstreams, "Upstream Providers", "OpenAI / Anthropic dialect endpoints.")
    System_Ext(credDb, "Credential Database (optional)", "PostgreSQL.")

    Rel(operator, providerAdmin, "Routing config CRUD", "HTTPS")
    Rel(operator, credAdmin, "Secret CRUD", "HTTPS")
    Rel(operator, overrideAdmin, "Arm/clear override", "HTTPS")
    Rel(providerAdmin, registry, "Mutates")
    Rel(credAdmin, credStore, "Mutates")
    Rel(overrideAdmin, credStore, "Reads active credential")
    Rel(caller, resolver, "Proxied request", "HTTPS")
    Rel(resolver, registry, "Reads current providers")
    Rel(resolver, forwarder, "Route decision")
    Rel(forwarder, credStore, "Active credential (passthrough only)")
    Rel(forwarder, upstreams, "Forwards", "HTTPS")
    Rel(credStore, credDb, "When configured", "EF Core / Npgsql")
```
