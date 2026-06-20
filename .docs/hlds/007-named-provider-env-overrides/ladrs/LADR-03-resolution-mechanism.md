# LADR-03: Resolution mechanism, key normalization, and precedence

**Status:** Draft

<!-- Status lifecycle: Draft → Prototype → Accepted. Also: "Superseded by LADR-MM", "Deprecated". -->

## Context

LADR-02 commits to a conventional `<NAME>_<FIELD>` env surface but leaves the *how* open: how a
provider key becomes an env prefix, how the convention is layered onto the bound options, what
wins when both the conventional and structured paths set the same field, and how the hard cutover
(LADR-01) avoids silently mis-binding a legacy array.

## Decision

**Resolve** the conventional surface with an `IPostConfigureOptions<ImposterOptions>` that runs
after the binder has produced the dictionary, reads `IConfiguration` (which already includes
environment variables), and writes derived values onto each provider. Post-configure is chosen
over a custom `IConfigurationSource` because it operates on the already-bound, name-keyed
dictionary — it can iterate the real provider keys rather than re-implementing section discovery.

**Key → env-prefix normalization.** Uppercase the key and replace every run of non-alphanumeric
characters with a single underscore: `opencode-go` → `OPENCODE_GO`, `opencode.anthropic` →
`OPENCODE_ANTHROPIC`. The lookup against actual environment variables is case-insensitive.

**Precedence (highest wins).**

1. Conventional env var (`OPENCODE_GO_API_KEY`)
2. Structured env var (`Imposter__Providers__opencode-go__Secret`)
3. `appsettings.json` value

The conventional var is the operator's most explicit, most intentional action, so it sits on top.
Because post-configure cannot see *which layer* supplied the already-bound value, the
implementation reads the conventional var directly from `IConfiguration`/environment and applies
it when present; if absent, the bound value (structured env or appsettings) stands. This yields
the ordering above without the resolver having to diff config layers.

**Legacy-shape guard.** A `Dictionary<string,T>` binds a JSON array as keys `"0"`, `"1"`, … —
so an un-migrated `Providers: [ ... ]` would bind silently with numeric keys. The validator
**rejects** purely numeric / sequential keys at startup with a message pointing at the migration
(see [NFR-02](../nfrs/NFR-02-migration-safety.md)).

## Alternatives Considered

- **Custom `IConfigurationSource`** — would let the convention participate in normal config
  precedence, but must re-discover providers before they are bound; more code, worse ergonomics.
- **Fill-only-when-empty precedence** — conventional var ignored if the field already has any
  value; rejected because it makes the friendly path the *weakest*, defeating "easier overrides".

## Consequences

- One small, testable post-configure type; no custom binder or config source.
- Deterministic, documented precedence; conventional var is authoritative when set.
- A field set by *both* conventional and structured env vars resolves to the conventional value —
  which could surprise an operator who set the structured one; documented, and a candidate for a
  startup `Warning` (Open).
- Numeric-key rejection turns the silent array-misbind into a fail-fast with guidance.

## Open

- **Double-set warning** — whether to log a `Warning` when a field is set by both paths, or stay
  silent and rely on documented precedence (owner: implementer; trigger: review).

## Related

- **LADR-01** / **LADR-02** — this LADR makes both concrete.
- **NFR-01** (override stability), **NFR-02** (migration safety), **NFR-03** (secret confidentiality).
