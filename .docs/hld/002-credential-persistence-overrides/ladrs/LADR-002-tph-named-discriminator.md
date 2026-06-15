# LADR-002 — TPH with a named discriminator over a shared base

- **Date / Status:** 2026-06-15 · Proposed

## Context

OpenAI and Anthropic credentials share almost all fields (`Name`, secret, scheme, active flag, base-url
override) and differ only in small dialect specifics (e.g. Anthropic `anthropic-version`). The router selects
a credential by dialect, so the discriminator is a first-class query dimension, not an implementation detail.

## Decision

Model credentials as **table-per-hierarchy (TPH)** under an abstract `ProviderCredential : BaseEntity`, with
`OpenAiCredential` and `AnthropicCredential` subtypes. Use an **explicitly named** discriminator column
`ProviderDialect` with stable string values (`openai`, `anthropic`) rather than the EF default `Discriminator`
column / CLR type name.

```csharp
builder.HasDiscriminator<string>("ProviderDialect")
       .HasValue<OpenAiCredential>("openai")
       .HasValue<AnthropicCredential>("anthropic");
```

Alternatives rejected: TPT (table-per-type) — extra joins and tables for one or two differing columns; a
single non-inherited entity with a free `string Dialect` — loses compile-time subtype safety and the clean
place to hang dialect-specific fields.

## Consequences

- One physical table `ProviderCredentials`; dialect-specific columns are nullable.
- Discriminator values are part of the persisted contract — renaming them is a data migration, so they are
  fixed to the lowercase dialect tokens already used across the routing config (`openai` / `anthropic`).
- New dialects extend the hierarchy with a new `HasValue<>` mapping and discriminator token.
