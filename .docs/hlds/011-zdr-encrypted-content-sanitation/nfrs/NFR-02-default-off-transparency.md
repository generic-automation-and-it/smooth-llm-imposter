# NFR-02: Flag off ⇒ forward path identical to pre-HLD

**Status:** Accepted

## Requirement

With `StripEncryptedContent` unset or `false` — the default for every provider that does
not explicitly opt in — the OpenAI forward path must behave exactly as it did before this
HLD:

- reasoning items in `input`, including ZDR items carrying `encrypted_content`, are
  forwarded unchanged;
- no additional array walk, parse, or allocation occurs (the branch is a single boolean
  comparison against `ProviderRoute.StripEncryptedContent`);
- session stamping (HLD 009), caching injection, `RequestNormalization` (HLD 004), and the
  `chat_completions` downgrade produce byte-identical output to the pre-HLD binary.

With the flag **on**, the same must hold for every part of the body other than matched ZDR
reasoning items: `instructions`, `messages`, a scalar `input`, non-reasoning input items,
plaintext/summary reasoning items, and the top-level `reasoning` config object are all
preserved.

## Rationale

The router's core property (HLD 001) is transparency. A default-off body mutation earns its
place only if it is invisible when not requested and surgical when requested — otherwise
every provider pays for one upstream's limitation.

## Verification

- L0 `RequestTransformerTests`: flag unset ⇒ a ZDR reasoning item survives with its
  `encrypted_content` intact; flag on ⇒ only the ZDR item is removed, and a body with
  `instructions` + scalar `input` + top-level `reasoning` is unchanged.
- L0 `RequestTransformerTests`: flag on + `OpenAiUpstreamApi: chat_completions` ⇒ strip
  then downgrade, with the surviving user turn intact.
- L0 `ProviderCatalogTests`: the flag materializes onto `ProviderRoute` through the HLD 008
  registry seed for `true` / `false` / unset.
- L2 `ProviderConfigAdminIntegrationTests`: the flag round-trips through
  `GET /admin/providers/{key}` and back into `PUT`, so a CRUD write cannot silently clear it.
- The pre-existing L0 + L2 suites (session forwarding, caching, normalization, downgrade,
  SSE) pass unchanged with the option present.

## Acceptance Criteria

- Every pre-HLD test passes without modification to its assertions.
- A provider with the flag unset produces the same forwarded body, byte for byte, as before
  this HLD for both ZDR and non-ZDR payloads.
- With the flag on, a body containing no ZDR reasoning item is forwarded unchanged.

## Applies To

- Goal 1 (Per-provider opt-in, default off), Goal 3 (Scoped strictly to ZDR blocks).
- [LADR-02](../ladrs/LADR-02-independent-per-provider-opt-in.md) — the opt-in shape that
  makes default-off achievable without coupling to other switches.
