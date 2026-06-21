# Named Provider Config & Conventional Env Overrides — High-Level Design

| | |
|---|---|
| **Status** | Completed |
| **Owner** | @generic-automation-and-it/project |
| **Tracker** | TBD — link the GitHub issue when filed |
| **Last updated** | 2026-06-21 |

> Discovery / prototyping HLD. This document delivers **intent + spec** — what we are
> building and why, the decisions behind it, and the quality bar it must meet. It does
> **not** contain an implementation plan; execution (phasing, sub-issues, sequencing) is
> tracked in the issue/work tracker.

## Intent

Provider configuration is an array (`Imposter:Providers: [ ... ]`), so every environment override
is addressed by **list index** — `Imposter__Providers__2__Secret`. That index is positional: reorder
the providers, insert one, or ship a different default set, and every `__N__` override silently
re-points at the wrong provider. This HLD makes provider identity **name-based**: the providers
collection becomes a `Dictionary<string, ProviderOptions>` keyed by provider name, and a
conventional per-provider env surface (`OPENCODE_GO_API_KEY`, `OPENCODE_GO_BASE_URL`, …) is layered
on top so operators get the `<THING>_API_KEY` convention they already expect. The request path is
untouched — this is a startup-configuration change only.

## Key Goals

### 1. Name-keyed providers replace the positional array

`ImposterOptions.Providers` becomes a `Dictionary<string, ProviderOptions>` whose key is the
provider's stable identity. Structured overrides become `Imposter__Providers__opencode-go__Secret`
and survive any reordering. This is a hard cutover — the array shape is removed, not dual-supported
— because the router is pre-1.0, stateless, and key-less, so there is no persisted config to
migrate. The `Name` field is retained as an **optional** override (key supplies the name unless
`Name` is set). The .NET binder maps a dictionary natively, so no custom binder is introduced for
the structural change. See [LADR-01](./ladrs/LADR-01-dictionary-keyed-providers.md).

**Acceptance criteria / DoD**

- `ImposterOptions.Providers` is a `Dictionary<string, ProviderOptions>`; the key is the provider name.
- An override addressed by name applies to the same provider regardless of declaration order.
- A provider with no `Name` takes its name from the key; a set `Name` overrides it; a blank `Name` is rejected.
- An un-migrated array config fails fast at startup with a message naming the new shape (no silent numeric-key bind).
- No `Imposter__Providers__<index>__` addressing remains in source, `appsettings*.json`, or docs.

### 2. Conventional `<NAME>_<FIELD>` env override surface

A provider keyed `opencode-go` exposes a conventional env prefix `OPENCODE_GO_`, mapping suffixes
to fields across the **full** scalar surface — `_API_KEY` → `Secret`, `_BASE_URL` → `BaseUrl`,
`_AUTH_SCHEME` → `AuthScheme`, plus `_DIALECT`, `_IS_DEFAULT`, `_OPENAI_UPSTREAM_API`,
`_REQUEST_NORMALIZATION`, `_ANTHROPIC_VERSION`. Matching is case-insensitive. The convention is
additive — `appsettings.json` and the structured `Imposter__Providers__<name>__*` path keep working
— and is realized by a startup post-configure step with documented precedence: conventional env >
structured env > appsettings. Model mappings stay on the structured path. See
[LADR-02](./ladrs/LADR-02-conventional-env-surface.md) and
[LADR-03](./ladrs/LADR-03-resolution-mechanism.md).

**Acceptance criteria / DoD**

- `OPENCODE_GO_API_KEY` populates the `opencode-go` provider's `Secret`; the equivalent holds for every mapped scalar field.
- Matching is case-insensitive; the provider key normalizes to the env prefix (non-alphanumeric → `_`, uppercased).
- When the same field is set conventionally and structurally, the conventional value wins, deterministically.
- A resolved secret value never appears in logs or error messages.
- Serving config resolution opens zero DB connections and issues zero upstream requests.

## Core Separation of Concerns

> Provider **identity** is a name, never a position; an **override** names the provider it targets.

The array conflated declaration order with identity, so the override surface was coupled to a list
index that no operator thinks in. This HLD draws the seam between *how providers are declared* and
*how they are addressed for override*: declaration is a named dictionary, addressing is by that
name through two equivalent env paths (structured and conventional). The request/forwarding path is
deliberately on the other side of the seam — unchanged.

## Guiding Principle — Address by name, never by index

> An operator overrides a provider by its name — the same name in the file, the structured var, and the convention.

- Provider identity comes from one place — the dictionary key (optionally overridden by `Name`).
- The conventional surface is *derived* from that key, so there is nothing extra to keep in sync.
- This HLD deliberately does **not** change the request path, model-mapping structure, dual-support
  the legacy array, or add per-field config to configure the convention.

---

## Diagrams

- [System Context (C1) + Configuration resolution & precedence flow](./diagrams/c4-context.md)

## Architecture Decisions (LADRs)

LADRs 01–02 are strategic (*what* and *why*); LADR-03 is tactical (*how*). Each is a single
decision — a horizontal concern spanning this HLD. See [`./ladrs/`](./ladrs/).

| LADR | Decision | Status |
|------|----------|--------|
| [LADR-01](./ladrs/LADR-01-dictionary-keyed-providers.md) | Key providers by name (Dictionary), hard cutover; `Name` optional override | Prototype |
| [LADR-02](./ladrs/LADR-02-conventional-env-surface.md) | Conventional `<NAME>_<FIELD>` env surface, case-insensitive, full field set | Prototype |
| [LADR-03](./ladrs/LADR-03-resolution-mechanism.md) | Post-configure resolver, key→prefix normalization, precedence, legacy-shape guard | Prototype |

## Non-Functional Requirements

Each NFR is a horizontal quality concern spanning the whole design, with a measurable target, a
verification mechanism, and acceptance criteria. See [`./nfrs/`](./nfrs/).

| NFR | Attribute | Target (summary) | Status |
|-----|-----------|------------------|--------|
| [NFR-01](./nfrs/NFR-01-override-stability.md) | Override Stability | Override targets same provider under any declaration order; no positional addressing | Prototype |
| [NFR-02](./nfrs/NFR-02-migration-safety.md) | Migration Safety | Legacy array / numeric / case-dup keys fail fast at startup with guidance | Prototype |
| [NFR-03](./nfrs/NFR-03-secret-confidentiality.md) | Security | Resolved secret values never logged or surfaced in errors | Prototype |
| [NFR-04](./nfrs/NFR-04-statelessness.md) | Statelessness | Resolution is config/env only — zero DB connections, zero upstream calls | Prototype |
