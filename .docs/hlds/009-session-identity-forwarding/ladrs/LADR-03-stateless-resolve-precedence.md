# LADR-03 — Stateless resolve precedence

<!-- Status lifecycle: Draft → Prototype → Accepted. -->

- **Status:** Accepted · 2026-07-24
- **Context:** NFR-001 forbids stored session affinity. Clients differ in what they send.
- **Decision:** Per-request precedence — headers (`session_id`, `x-opencode-session`, `x-session-id`, `conversation_id`) → body (`prompt_cache_key`, `metadata.user_id`) → SHA-256 fingerprint of stable caller identity material → none. Never random. Log only the source token.
- **Fingerprint inputs:** `chatgpt-account-id`, `openai-organization`, `openai-project`, `Authorization`, `x-api-key`, body `user`. Sorted canonical `name=value` lines; when **at least one** input is present, output `derived-` + 16-byte SHA-256 prefix (32 hex chars). When **all six are absent**, the resolver returns `session=none` — never a hash of the empty input set.
- **Consequences:** Same CLI credential/account buckets together upstream; empty identity stays unstamped.

## Alternatives Considered

- **In-memory per-caller bucket keyed on credential hash.** Rejected:
  violates NFR-01 statelessness and re-introduces the persistence
  SmoothLlmImposter is built to avoid.
- **Fingerprint headers only, omit body `user`.** Rejected: would bucket
  distinct `user` requests from the same account together, breaking
  per-user cache isolation.

## Related

- [LADR-01: Fourth rewrite class, opt-in](./LADR-01-fourth-rewrite-class-opt-in.md)
- [NFR-01: Statelessness](../nfrs/NFR-01-statelessness.md)
- `SessionIdentityResolver.FingerprintHeaderNames`

## Known Limitations

- **Credential rotation caveat:** Rotating the CLI credential (e.g.
  `claude setup-token`, API key refresh) will fork the opencode-go
  session identity, because the full `Authorization` value is one of the
  fingerprint inputs. If a stable caller bucket across key rotation is
  needed, fingerprint only the scheme + a stable suffix of the value.
