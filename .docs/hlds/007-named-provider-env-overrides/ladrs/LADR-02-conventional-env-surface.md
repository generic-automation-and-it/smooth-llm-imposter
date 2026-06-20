# LADR-02: Conventional `<NAME>_<FIELD>` env override surface

**Status:** Prototype

<!-- Status lifecycle: Draft → Prototype → Accepted. Also: "Superseded by LADR-MM", "Deprecated". -->

## Context

Even with name-keyed providers (LADR-01), the structured override path is verbose and unfamiliar:
an operator who just wants to inject a key must write `Imposter__Providers__opencode-go__Secret`.
Operators expect the ecosystem convention — `OPENAI_API_KEY`, `<THING>_API_KEY` — and reach for
it first. The double-underscore section path is also awkward to type, easy to mis-case, and
unique to .NET configuration.

## Decision

**Add** a conventional, provider-scoped environment-variable surface derived from the provider
key. For a provider keyed `opencode-go`, the convention exposes an env prefix `OPENCODE_GO_` and
maps conventional suffixes onto provider fields:

| Env suffix | Provider field |
|------------|----------------|
| `_API_KEY` | `Secret` |
| `_BASE_URL` | `BaseUrl` |
| `_AUTH_SCHEME` | `AuthScheme` |
| `_DIALECT` | `Dialect` |
| `_IS_DEFAULT` | `IsDefault` |
| `_OPENAI_UPSTREAM_API` | `OpenAiUpstreamApi` |
| `_REQUEST_NORMALIZATION` | `RequestNormalization` |
| `_ANTHROPIC_VERSION` | `AnthropicVersion` |

Matching is **case-insensitive** and the **full field surface** is covered, not just the secret —
the secret (`_API_KEY`) is simply the most common entry point. The convention is additive: the
structured `Imposter__Providers__<name>__<Field>` path and `appsettings.json` continue to work
unchanged. Model mappings (`Models[]`) are **out of scope** for the convention — they are
structured collections, not scalar per-provider knobs, and stay on the structured path.

Key→prefix normalization, the post-configure mechanism, and the precedence tie-break between the
conventional and structured paths are tactical concerns specified in
[LADR-03](./LADR-03-resolution-mechanism.md).

## Alternatives Considered

- **Secret-only convention (`<NAME>_API_KEY`)** — covers the 90% case but forces operators back
  onto the verbose path for `AuthScheme`/`BaseUrl`, which flip per environment; rejected per the
  "full surface" decision.
- **Hard-coded global names (`OPENAI_API_KEY`)** — ambiguous when several providers share a
  dialect (two OpenAI upstreams); the per-provider prefix disambiguates.
- **Configurable prefix/suffix scheme** — more flexible but adds config-to-configure-the-config
  surface; the derived-from-key convention is zero-config and predictable.

## Consequences

- The friendly path operators expect (`OPENCODE_GO_API_KEY`) works out of the box, per provider.
- Case-insensitivity sidesteps the dict-key casing footgun (LADR-01) — `OPENCODE_GO_API_KEY`
  resolves to the `opencode-go` provider regardless of env-var casing.
- Two env mechanisms now exist for the same field; precedence must be defined and documented so
  a value set both ways is deterministic (LADR-03, with the tie-break as an Open item).
- The suffix→field table is a maintenance point: a new scalar `ProviderOptions` field must be
  added here too, or it silently lacks a conventional override.

## Open

- **Field-name drift** — adding a `ProviderOptions` scalar without updating the suffix map leaves
  it convention-less. **Resolved (Prototype):** `ImposterOptionsPostConfigureTests`'
  `Every_bindable_scalar_field_has_a_mapped_suffix` reflects over `ProviderOptions`' string/bool
  properties (excluding the identity field `Name`) and asserts each has a mapped suffix, so a new
  scalar fails the test until it is mapped.

## Related

- **LADR-01** — provides the stable keys this convention derives prefixes from.
- **LADR-03** — the mechanism and precedence that make this convention concrete.
