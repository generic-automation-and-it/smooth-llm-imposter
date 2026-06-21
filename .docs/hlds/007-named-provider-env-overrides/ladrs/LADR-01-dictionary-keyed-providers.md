# LADR-01: Key providers by name, not array index

**Status:** Accepted

<!-- Status lifecycle: Draft → Prototype → Accepted. Also: "Superseded by LADR-MM", "Deprecated". -->

## Context

`ImposterOptions.Providers` is a `List<ProviderOptions>`. The .NET configuration binder
therefore addresses each provider **positionally**, so every environment override is index-bound:
`Imposter__Providers__2__Secret` targets "opencode-go" only while it happens to sit at index 2.
The shipped `appsettings.json` order, an inserted provider, or a different default set silently
re-points every `__N__` override at the wrong provider. The setup docs are full of `__2__ / __3__
/ __4__`, which makes this a live operator footgun rather than a theoretical one.

## Decision

**Adopt** `Dictionary<string, ProviderOptions>` for `ImposterOptions.Providers`, keyed by the
operator-facing provider name. The dictionary key becomes the provider's stable identity:
structured overrides become `Imposter__Providers__opencode-go__Secret`, which survives any
reordering or insertion.

This is a **hard cutover** — the array shape is removed, not dual-supported. The router is
pre-1.0, stateless, and key-less, so there is no persisted config to migrate and no compatibility
contract to honour. The `Name` property is **retained as an optional override**: when set it wins,
otherwise the key supplies the name. This keeps a single source of truth (the key) while leaving
an escape hatch for a display name that differs from the override key.

The binder maps a `Dictionary<string,T>` natively — no custom binder, config source, or parsing
is introduced for the structural change. The whole structural fix is a type change plus its
fan-out into the validator, catalog, and docs.

## Alternatives Considered

- **Keep `List`, add a name→index resolver** — still leaves the index-based env surface as the
  documented default; the instability persists for anyone using the structured path.
- **Dual-support List and Dictionary for one release** — doubles the binding/validation surface
  and the docs for a router with no installed base to protect.
- **Drop `Name` entirely (key is the only name)** — cleaner, but removes the ability to give a
  provider a display name distinct from its override key; rejected in favour of key-default +
  optional override.

## Consequences

- Overrides are name-addressable and stable across config reordering (the core goal).
- Breaking change to the config schema: existing `Providers: [ ... ]` files must become
  `Providers: { "<name>": { ... } }`. Mitigated by LADR-03's legacy-shape detection.
- The validator's duplicate-name check becomes mostly free — dict keys are unique — but a
  case-only collision (`opencode-go` vs `OpenCode-Go`) is still possible and must be caught
  (see [NFR-02](../nfrs/NFR-02-migration-safety.md)).
- `Name` being optional means two ways to express a name; the validator must define which wins
  (key default, `Name` override) and reject a blank `Name` override.

## Related

- **LADR-02** — the conventional env surface that builds on these stable keys.
- **LADR-03** — the resolution mechanism, key→env-prefix normalization, and legacy-shape guard.
