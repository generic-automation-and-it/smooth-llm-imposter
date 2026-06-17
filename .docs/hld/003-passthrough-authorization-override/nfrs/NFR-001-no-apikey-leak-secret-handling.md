# NFR-001: Security — no `x-api-key` leak, secret handled just-in-time

**Status:** Draft

<!-- Horizontal security concern for the override path. Lifecycle: Draft → Prototype → Accepted. -->

## Requirement

When a dialect's override is **ON**, every outbound passthrough request for that dialect carries
`Authorization: Bearer <secret>` and **no `x-api-key` header** — a binary assertion, verified on the wire.
The active credential's plaintext secret is decrypted only just-in-time at forward (never persisted in
plaintext, never logged, never returned by any endpoint), exactly as HLD 002 already requires
([HLD 002 NFR-001](../../002-credential-persistence-overrides/nfrs/NFR-001-secret-encryption-at-rest.md)).
Matched imposter requests are unaffected and continue to apply the config key.

## Verification

- Integration test (in-process stub upstream): with the override ON, assert the captured outbound passthrough request **has** an `Authorization: Bearer …` header and **does not** contain `x-api-key`; with it OFF, assert HLD 002 behaviour.
- Integration test: a stored credential with `AuthScheme = ApiKey` is still forwarded as `Bearer` while ON.
- Log assertion / review: toggle and forward log lines contain dialect + action only — never the secret or the bearer value.

## Acceptance Criteria

- Override ON ⇒ outbound passthrough request has `Authorization: Bearer`, never `x-api-key`.
- Secret appears only in the outbound `Authorization` header at forward time — not in logs, error bodies, or the `GET` state response.
- A matched imposter request's headers are identical regardless of the switch.

## Applies To

Goal 2 (force Bearer, passthrough only); the passthrough branch of the routing/forwarding flow. Reaffirms
HLD 002 NFR-001 for the new forced-Bearer case.
