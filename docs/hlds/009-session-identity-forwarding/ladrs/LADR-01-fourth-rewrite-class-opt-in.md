# LADR-01 — Fourth sanctioned rewrite class, opt-in

<!-- Status lifecycle: Draft → Prototype → Accepted. -->

- **Status:** Accepted · 2026-07-24
- **Context:** Transparent proxy non-negotiable allows only named rewrite classes. Session stamping must not leak onto passthrough.
- **Decision:** Add `SessionForwarding` as a fourth request-rewrite class. Apply only when `decision.IsImposter` and provider `SessionForwarding == OpencodeGo`. Default `None`.
- **Consequences:** ROUTING_AGENTS.md wording updates from three to four classes; L0/L2 tests pin opt-in and passthrough transparency.

## Alternatives Considered

- **Fold session-stamping into the existing auth rewrite class** (the
  forwarder already manages a header). Rejected: conflates "who is
  calling" with "what credential" — auth and session identity have
  different opt-in semantics and different log-discipline requirements
  (NFR-03).
- **Session-stamp on passthrough too** (so all routes get a resolved
  session). Rejected: breaks NFR-02 byte-transparency on default and
  leaks caller identity on routes the operator did not configure.

## Related

- [LADR-02: Dual-stamp and cache key](./LADR-02-dual-stamp-and-cache-key.md)
- [NFR-01: Statelessness](../nfrs/NFR-01-statelessness.md)
- [NFR-02: Safe-default transparency](../nfrs/NFR-02-safe-default-transparency.md)
