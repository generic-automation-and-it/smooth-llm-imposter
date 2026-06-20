# NFR-004 — Configuration Precedence & Validation

- **Category:** Operability
- **Status:** Accepted · 2026-06-14

Configuration binds from the `Imposter` section; **environment variables override
`appsettings.json`** (env wins). Providers are keyed by **name** (HLD 007), so overrides are
name-addressed: `Imposter__Providers__openrouter-anthropic__Secret=sk-...`, or the conventional
`OPENROUTER_API_KEY=sk-...` shared by configured dialect-suffixed siblings such as `openrouter-openai`
and `openrouter-anthropic` (conventional > structured > appsettings). Each provider's `AuthScheme`
(`ApiKey`|`Bearer`, case-insensitive) selects the auth header and defaults by dialect when omitted
(openai → Bearer, anthropic → ApiKey).

Options are validated fail-fast at startup (`ValidateOnStart`): unknown dialects, non-absolute
base URLs, duplicate provider names/keys (case-insensitive), a legacy array / numeric-key shape,
malformed model mappings, and more than one default per dialect are all rejected before the app
begins serving traffic.
