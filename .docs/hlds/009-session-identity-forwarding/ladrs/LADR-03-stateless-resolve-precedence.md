# LADR-03 — Stateless resolve precedence

- **Status:** Accepted · 2026-07-24
- **Context:** NFR-001 forbids stored session affinity. Clients differ in what they send.
- **Decision:** Per-request precedence — headers (`session_id`, `x-opencode-session`, `x-session-id`, `conversation_id`) → body (`prompt_cache_key`, `metadata.user_id`) → SHA-256 fingerprint of stable caller identity material → none. Never random. Log only the source token.
- **Fingerprint inputs:** `chatgpt-account-id`, `openai-organization`, `openai-project`, `Authorization`, `x-api-key`, body `user`. Sorted canonical `name=value` lines; output `derived-` + 16-byte hex prefix.
- **Consequences:** Same CLI credential/account buckets together upstream; empty identity stays unstamped.
