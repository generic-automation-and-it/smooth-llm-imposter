# LADR-01 — Fourth sanctioned rewrite class, opt-in

- **Status:** Accepted · 2026-07-24
- **Context:** Transparent proxy non-negotiable allows only named rewrite classes. Session stamping must not leak onto passthrough.
- **Decision:** Add `SessionForwarding` as a fourth request-rewrite class. Apply only when `decision.IsImposter` and provider `SessionForwarding == OpencodeGo`. Default `None`.
- **Consequences:** ROUTING_AGENTS.md wording updates from three to four classes; L0/L2 tests pin opt-in and passthrough transparency.
