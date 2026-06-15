# Diagram — System Context

Adds a PostgreSQL store to the HLD 001 context. Upstream LLM providers are unchanged; the store holds
**only passthrough credentials**, encrypted at rest.

```mermaid
C4Context
    title SmoothLlmImposter — Credential Persistence (HLD 002)

    Person(operator, "Operator", "Manages stored passthrough credentials via the admin API")
    Person(client, "LLM SDK client", "OpenAI- or Anthropic-dialect caller")

    System(router, "SmoothLlmImposter", "Stateless router + credential admin API")

    System_Ext(openai, "OpenAI-dialect upstream", "Real / imposter OpenAI servers")
    System_Ext(anthropic, "Anthropic-dialect upstream", "Real / imposter Anthropic servers")
    SystemDb_Ext(pg, "PostgreSQL", "Encrypted passthrough credentials (TPH)")

    Rel(client, router, "model calls", "HTTPS")
    Rel(operator, router, "manage credentials", "HTTPS /admin")
    Rel(router, pg, "read active / write credentials", "EF Core")
    Rel(router, openai, "forward (key from config or stored passthrough)", "HTTPS")
    Rel(router, anthropic, "forward (key from config or stored passthrough)", "HTTPS")
```

> The imposter (matched) path still draws keys from configuration only. PostgreSQL participates **only** in
> the passthrough/default path and the admin API.
