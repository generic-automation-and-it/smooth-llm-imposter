# NFR-004 — Configuration Precedence & Validation

- **Category:** Operability
- **Status:** Accepted · 2026-06-14

Configuration binds from the `Imposter` section; **environment variables override
`appsettings.json`** (env wins), e.g. `Imposter__Providers__4__ApiKey=sk-...`.

Options are validated fail-fast at startup (`ValidateOnStart`): unknown dialects, non-absolute
base URLs, duplicate provider names, malformed model mappings, and more than one default per
dialect are all rejected before the app begins serving traffic.
