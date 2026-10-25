# AGENTS.md - Named Provider Config & Conventional Env Overrides

AI Context: HLD for Named Provider Config & Conventional Env Overrides. Updated: 2026-06-21

> AI-coder context for this HLD. Architecture diagrams live in [`./diagrams/`](./diagrams/),
> decisions in [`./ladrs/`](./ladrs/), quality spec in [`./nfrs/`](./nfrs/). This file is
> guardrails, not narrative — the narrative is in [`./README.md`](./README.md).

## TL;DR

Provider config moves from a positional `List<ProviderOptions>` to a name-keyed
`Dictionary<string, ProviderOptions>`, plus a conventional `<NAME>_<FIELD>` env override surface.
Startup-config change only — the request/forwarding path does not change.

## Non-Negotiables

- Providers are keyed by **name**, never by index. Do not reintroduce `__N__` / positional addressing anywhere (source, appsettings, docs).
- Hard cutover: there is **no** dual-support for the legacy array. An array / numeric-key / sequential-key config must **fail fast** at startup, not bind silently.
- `Name` is an optional display label, not a required duplicate. Key supplies the display name unless `Name`
  is set; HLD 008 credentials/override use the stable key. A blank `Name` is invalid.
- Conventional env precedence is fixed: **conventional env > structured env > appsettings**. Do not make the conventional path the weakest ("fill-only-if-empty").
- A resolved **secret value** is never logged or placed in an exception/validation message (NFR-03).
- Resolution is config/env only — no DB, no network, no persisted state (NFR-04). Keep the router stateless/key-less.
- HLD is Completed (implemented + tested); LADRs are load-bearing — flag deviations rather than silently overriding.

## Architecture Decisions

Only decisions whose violation produces wrong code. Full records in [`./ladrs/`](./ladrs/).

| LADR | Decision | Why it matters |
|------|----------|----------------|
| LADR-01 | Dictionary-keyed providers, hard cutover; `Name` optional display label | Reverting to a list re-creates the positional-override bug this HLD exists to kill |
| LADR-02 | Conventional `<NAME>_<FIELD>` env surface, case-insensitive, full scalar field set (incl. `_AUTHORIZATION_BEARER` secret alias) | A missing suffix mapping silently leaves a field with no conventional override |
| LADR-03 | Post-configure resolver; key→prefix normalization; precedence; legacy-shape guard | Wrong precedence or missing guard yields silent mis-binding instead of fail-fast |
| LADR-04 | Named personal-subscription providers (`anthropic-personal` / `openai-personal`), Bearer secret, not `IsDefault` | Setting `IsDefault` collides with the dialect default; a missing `Models` makes a non-default provider unreachable |

## Key Behaviors

- `Dictionary<string,T>` binds a JSON **array** as numeric keys `"0","1",…`. The validator must reject numeric/sequential keys, or an un-migrated array binds silently with the same positional bug.
- Config dictionaries bind ordinally — `opencode-go` and `OpenCode-Go` are distinct keys. Validate case-only duplicates; the conventional resolver matches env names case-insensitively.
- Key→env-prefix normalization: uppercase, every run of non-alphanumeric → single `_` (`opencode-go` → `OPENCODE_GO`).
- `Models[]` mappings are **out of scope** for the conventional surface — structured path only.

## Quality Constraints

Measurable NFRs live in [`./nfrs/`](./nfrs/). Constraints that change how code is written:

- Provider identity must be order-independent end to end — no code path may key a provider by list position (NFR-01).
- Secret handling reuses the existing key-less/no-persist posture; the resolver adds no new sink for secret values (NFR-03/04).

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-06-20 | HLD scaffolded and drafted | TBD |
| 2026-06-20 | Implemented: `Providers` → `Dictionary<string, ProviderOptions>`; `ImposterOptionsPostConfigure` conventional `<NAME>_<FIELD>` resolver; validator legacy-array/numeric-key + case-dup + blank-`Name` guards; appsettings + all setup docs rewritten to name-keyed. LADRs/NFRs Draft→Prototype. Open items resolved: LADR-02 field-drift → scalar-coverage guard test; LADR-03 double-set warning → **no warning** (silent, documented precedence). | TBD |
| 2026-06-21 | Added LADR-04: named personal-subscription providers `anthropic-personal` (captures `claude-opus-4-7*`→`claude-opus-4-8` on the operator's own Bearer token) and `openai-personal` (inert codex passthrough template), Bearer + empty committed `Secret`, neither `IsDefault`. `openrouter-anthropic`'s glob narrowed to `claude-opus-4-6*` so the Opus globs stay distinct. Extended LADR-02 with the `_AUTHORIZATION_BEARER` secret alias (`_API_KEY` canonical, first-present-wins). Resolver, appsettings, HLD 001 example, setup docs, and L0 tests updated. | #39 |
| 2026-06-21 | Status → **Completed** — shipped + tested (PR #42); LADR-01..04 + NFR-01..04 confirmed **Accepted**. | TBD |
