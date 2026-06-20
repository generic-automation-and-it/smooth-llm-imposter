# Diagrams — Named Provider Config & Conventional Env Overrides

The **C1 System Context** below is the mandatory floor. One supporting diagram is added: the
**configuration resolution flow**, because the precedence pipeline (appsettings → structured env →
conventional env → validate) is the load-bearing behaviour of this HLD and is not obvious from the
context view alone.

## System Context (C1)

This HLD changes only how the router *loads* its provider configuration at startup — it does not
change the request path. The operator supplies provider config three ways (file, structured env,
conventional env); the router binds and validates it into the in-memory route catalogue used by the
existing forwarder. No new external dependency is introduced.

```mermaid
C4Context
    title Named Provider Config and Conventional Env Overrides - System Context

    Person(operator, "Operator", "Configures providers and injects secrets per environment.")

    System_Boundary(router, "SmoothLlmImposter Router") {
        System(config, "Provider Config Resolution", "Binds named-dictionary providers; applies conventional env overrides; validates at startup.")
        System(routing, "Routing / Forwarder", "Existing request path - unchanged by this HLD.")
    }

    System_Ext(appsettings, "appsettings.json", "Base named-provider dictionary.")
    System_Ext(env, "Environment variables", "Structured plus conventional per-provider overrides.")

    Rel(operator, appsettings, "Authors")
    Rel(operator, env, "Sets per environment")
    Rel(appsettings, config, "Bound at startup")
    Rel(env, config, "Overrides at startup")
    Rel(config, routing, "Supplies validated route catalogue")
```

## Flow — Configuration resolution & precedence

How a single provider field's value is resolved at startup. The conventional var wins when present;
otherwise the bound value (structured env over appsettings) stands; legacy array shape fails fast.

```mermaid
flowchart TD
    A[Startup: bind Imposter section] --> B{Provider keys numeric or sequential?}
    B -->|yes| F[Fail fast: legacy array shape detected]
    B -->|no| C[Bind Dictionary of name to ProviderOptions: appsettings plus structured env merged]
    C --> D[PostConfigure per provider key]
    D --> E{Conventional env NAME_FIELD set?}
    E -->|yes| G[Apply conventional value - highest precedence]
    E -->|no| H[Keep bound value - structured env over appsettings]
    G --> I[Validate: unique names, dialect, base URL, mappings]
    H --> I
    I -->|fail| J[Fail fast with field-level messages]
    I -->|pass| K[ProviderCatalog: immutable routes]
```
