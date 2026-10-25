# AGENTS.md - ZDR Encrypted-Content Sanitation

AI Context: HLD for the `StripEncryptedContent` per-provider opt-in (HLD 011). **IMPLEMENTED**. Updated: 2026-08-10

## TL;DR

`StripEncryptedContent: true` on an OpenAI-dialect provider drops `reasoning` items in the
top-level `input` array whose `encrypted_content` is a non-null JSON node, before the body is
forwarded. The proxy has no decryption key, so it drops rather than decodes. Default off.
Intent in [README.md](./README.md), decisions in [ladrs/](./ladrs/), quality spec in
[nfrs/](./nfrs/).

## Implementation map

| Concern | Where |
|---|---|
| Option | `ProviderOptions.StripEncryptedContent` (`bool?`) — `Features/Routing/ImposterOptions.cs` |
| Env override | `_STRIP_ENCRYPTED_CONTENT`, bool path — `ImposterOptionsPostConfigure.cs` |
| Clone (HLD 008 registry) | `ProviderOptionsCloner.Clone` — **must** copy the field |
| Admin CRUD | `ProviderConfigurationResponse` / `ProviderConfigurationBody` — both directions |
| Materialization | `ProviderCatalog` → `ProviderRoute.StripEncryptedContent` |
| Strip | `OpenAiRequestTransformer.StripEncryptedReasoning` / `HasEncryptedContent` |

## Non-Negotiables

- **Never decrypt, decode, or log `encrypted_content`** (NFR-01). The predicate reads only
  whether the property exists with a non-null value; the value itself is never bound to a
  variable. No key material belongs on this path — ZDR holds the key server-side, so code
  that appears to decrypt is a bug, not a feature.
- **Drop the whole item, not the property** (LADR-01). An emptied
  `{"type":"reasoning","summary":[]}` carries nothing the upstream can use and is a shape no
  upstream is guaranteed to accept.
- **JSON `null` `encrypted_content` is not ZDR content.** Some clients serialize the property
  unconditionally. `ContainsKey` alone is the wrong predicate — it deletes the accompanying
  plaintext summary. Use the non-null check.
- **Do not couple to `OpenAiUpstreamApi`, `RequestNormalization`, or `IsImposter`** (LADR-02).
  The motivating upstream stays on `/responses`, where normalization is validator-forbidden.
  Making this a normalization profile or an imposter-gated branch breaks the use case.
- **Ordering is load-bearing: after normalization, before `ToChatCompletions`.** Moving the
  strip inside `ToChatCompletions` loses the `responses` path — the only path that matters
  for the motivating case.
- **Walk only the top-level `input` array.** The top-level `reasoning` object is *config*
  (`{"effort":"high"}`), not an input item; `instructions`, `messages`, and a scalar `input`
  are out of scope.
- **A new scalar `ProviderOptions` field must be added to `ProviderOptionsCloner.Clone` and
  to both CRUD DTOs.** This field shipped missing from both: the registry seed cloned it away,
  so the feature was inert in the real Host while every unit test passed (they built
  `ProviderRoute` by hand). The `ProviderOptionsClonerTests` drift guard now covers `bool?` —
  do not narrow that filter again.
- **Default off; enable by injection, not by committed config.** No `appsettings*.json` in this
  repo sets the flag; operators inject `<PROVIDER>_STRIP_ENCRYPTED_CONTENT=true` per
  deployment (LADR-03 documents the env parse semantics). Do not add a default-on value.

## Key Behaviors

- **Predicate:** `type == "reasoning"` (OrdinalIgnoreCase) **and** `encrypted_content` present
  **and** non-null. All three required.
- **Action:** `RemoveAt` on the `input` array, iterating **backwards** so indices stay valid.
- **Composition:** with `OpenAiUpstreamApi: chat_completions`, the strip runs first and
  `BuildMessages` only ever sees survivors — `RejectResponsesStatePointers` is unaffected
  because it inspects state pointers, not reasoning items.
- **Flag off:** a single boolean comparison; no walk, no allocation (NFR-02).
- **Bad env value:** `bool.TryParse` fails ⇒ logged, bound value unchanged, boot succeeds.
  The flag stays off and the upstream 400 persists — deliberate, see LADR-03.

## Quality Constraints

- **NFR-01 (Security):** no decryption attempt, no logging of stripped content.
- **NFR-02 (Transparency):** flag off ⇒ forward path identical to pre-HLD, byte for byte.
- **Coverage shape matters here.** Transformer-level tests alone were what let the inert-flag
  bug ship green. Any change to the option's plumbing needs a test that materializes through
  `ProviderCatalogTestFactory.SeededCatalog` (registry seed + clone) and, for the CRUD surface,
  the L2 `GET → PUT` round-trip.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-08-10 | Initial HLD AGENTS.md — 3 LADRs, 2 NFRs, 1 diagram file. Authored retrospectively alongside the review fixes (clone, CRUD DTOs, JSON-null predicate, drift guard, catalog + L2 coverage). | — |
