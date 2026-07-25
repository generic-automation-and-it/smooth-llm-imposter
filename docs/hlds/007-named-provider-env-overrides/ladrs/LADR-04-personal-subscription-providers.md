# LADR-04: Personal-subscription providers (`anthropic-personal` / `openai-personal`)

**Status:** Accepted

<!-- Status lifecycle: Draft → Prototype → Accepted. Also: "Superseded by LADR-MM", "Deprecated". -->

## Context

The shipped config has two provider archetypes. The `*-default` providers (`anthropic-default`,
`openai-default`) are **key-less catch-alls**: they hold no `Secret`, so on the passthrough path the
forwarder relays the caller's *own* `Authorization` / `x-api-key` (HLD 001 LADR-005). The
`openrouter-*` / `opencode-go-*` providers are **alternate-upstream remaps**: a model is rewritten to a
*different vendor's* model id and sent to that vendor's gateway with that vendor's key.

Neither archetype expresses a common operator need: *route a specific model family to my own
first-party subscription, billed to a different account than the daily one.* A team often runs a
**company** subscription as the default (every unmatched call uses the caller's company credential) but
wants private/after-hours work — e.g. all Opus traffic — to bill to a **personal** subscription token
instead. That is the same dialect, the same first-party endpoint (`api.anthropic.com` /
`chatgpt.com/backend-api/codex`), just a *different credential* — not a vendor remap, and not the
key-less default.

## Decision

**Add** two named providers that capture this archetype: **operator-owned subscription credentials**.

- **`anthropic-personal`** — `Dialect: anthropic`, `BaseUrl: https://api.anthropic.com` (identical to
  `anthropic-default`), `AuthScheme: Bearer`, `Secret: ""` in committed config (the operator supplies
  their own token via env — NFR-03). It carries one mapping, `claude-opus-4-7*` → `claude-opus-4-8`,
  `Caching: true`: it captures inbound Opus-4.7 calls and serves them as the operator's chosen Opus
  version (`claude-opus-4-8`) on the personal subscription. The `To` stays **within the Anthropic Opus
  family**, so this is *subscription capture* (optionally pinning a version), not a cross-vendor remap.
  A `Models` entry is nonetheless required because a non-default provider is only reachable via a
  `From` match — without it the provider could never be selected. The `From` glob is the **canonical**
  model id `claude-opus-4-7*` (not the shorthand `opus-4.7*`), because `ModelMatcher` does a literal
  case-insensitive prefix match and real inbound ids look like `claude-opus-4-7-20250930`.
- **`openai-personal`** — `Dialect: openai`, `BaseUrl: https://chatgpt.com/backend-api/codex` (matches
  `openai-default`), `AuthScheme: Bearer`, `Secret: ""`, and **no `Models`**. Codex personal is a
  straight credentialed passthrough archetype with no current capture target, so it ships **inert** (no
  mapping ⇒ never selected, exactly like the existing `openrouter-openai` with `Models: []`). The
  operator activates it by adding their own mappings or by pointing a client at it. The asymmetry with
  `anthropic-personal` is deliberate: the motivating case captures one Anthropic family (Opus) while the
  company default handles the rest; codex has no equivalent single-family capture today.

**Neither sets `OpenAiUpstreamApi`** — both first-party endpoints are `/responses`-native, so they keep
the byte-transparent `responses` default (downgrading to `chat_completions` would be wrong here).

**`AuthScheme: Bearer`** because a subscription token (e.g. from `claude setup-token`) is presented as
`Authorization: Bearer <token>`, in contrast to `opencode-go-anthropic`'s `ApiKey` (`x-api-key`) scheme.

**Secret env surface.** To make a Bearer subscription token read naturally, LADR-02's conventional
surface gains a second secret spelling: **`_AUTHORIZATION_BEARER`** (alias of `_API_KEY` → `Secret`). An
operator exports `ANTHROPIC_PERSONAL_AUTHORIZATION_BEARER` / `OPENAI_PERSONAL_AUTHORIZATION_BEARER` (the
prefix is still the provider key; `_API_KEY` remains valid and canonical). See LADR-02.

## Alternatives Considered

- **Reuse the key-less default + caller credential** — the default is all-or-nothing per dialect; it
  cannot bill a *subset* of models to a second subscription. Rejected: it can't express "Opus → personal,
  everything else → company".
- **Make the personal provider `IsDefault`** — collides with the existing `*-default` (at most one
  default per dialect, startup-validated) *and* would capture *all* traffic, not just the targeted
  family. Rejected.
- **Route through a third-party gateway (openrouter) with a personal key** — that is a different vendor
  and a different model; it does not use the operator's own first-party subscription. Rejected — it's the
  remap archetype, not credential capture.
- **A first-class `subscription` provider type / flag** — adds domain surface for what is fully
  expressible as a named provider with `Secret` + `Bearer`. Rejected per HLD 007's "no new config to
  configure the config" stance.

## Consequences

- An operator gets a ready-made template for "capture this family on my personal subscription" without
  touching the default. `Secret` stays empty in committed config (NFR-03); the token is supplied via
  env only.
- **Distinct Opus globs, no collision.** `anthropic-personal` matches `claude-opus-4-7*` while
  `openrouter-anthropic` matches `claude-opus-4-6*`, so each owns a different Opus minor version and they
  never compete. (If two same-dialect providers *did* share a glob, the resolver is first-match-wins in
  declaration order — so the one declared earlier in `appsettings.json` would win; order accordingly.)
- `openai-personal` is **inert as shipped** (no `Models`, not default) — consistent with the
  `openrouter-openai` empty-`Models` precedent. It validates fine (no rule requires `Secret`, `Models`,
  or default).
- No new validator rule: a non-default provider with `Secret: ""` and a well-formed mapping already
  passes the existing per-provider checks. Reuses NFR-02 (migration safety, unchanged — no shape change)
  and NFR-03 (secret confidentiality).

## Related

- **LADR-01** — name-keyed providers make `anthropic-personal` / `openai-personal` addressable identities.
- **LADR-02** — the conventional env surface, extended here with the `_AUTHORIZATION_BEARER` secret alias.
- **HLD 001 LADR-005** — the key-less default-passthrough archetype this one is contrasted against.
- **NFR-03** (secret confidentiality) — committed `Secret` stays empty; the token is env-supplied.
