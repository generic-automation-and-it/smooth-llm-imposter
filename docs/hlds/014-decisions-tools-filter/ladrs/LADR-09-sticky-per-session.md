# LADR-09: Opt-in sticky tool selection per session

**Status:** Accepted

## Context

- The decision filter re-selects tools on every user turn. When the kept set changes, the forwarded `tools`
  array changes, and every provider prompt cache that orders tools first (Anthropic: tools → system →
  messages; OpenAI-compatible prefix caches) loses the conversation prefix.
- Measured from captured Claude Code bodies: tools are 10k–23k tokens of a 25k–60k-token prefix. When the
  upstream caches, rewriting that prefix every user turn can cost more than the tokens the filter removes.
- HLD 001 LADR-008 only stabilises tool *order*; it never keeps a tool the request no longer carries, and it
  runs before this filter, so it cannot see the filter's removals.
- The harness re-sends its full catalog on every request, so keeping a tool needs no stored definition —
  only the decision not to remove it.

## Decision

`StickyPerSession` (`DECISIONS_TOOLS_STICKY_PER_SESSION`, default `false`) remembers, per session, which tools
the filter forwarded. Later requests in that session treat those tools like `AlwaysKeep`: they are not asked
about and not removed while the harness still supplies them. New tools are classified normally.

- **Identity:** a captured session identity (`SessionIdentitySource.Captured`) resolved from caller headers and
  the **inbound** body. The forwarded body is not used: OpenAI transforms inject `prompt_cache_key` = inbound
  model on caching routes, which would give every chat of that model one shared "session". Derived
  fingerprints and absent identities disable stickiness for that request.
- **Scope:** SHA-256 of body shape, forwarded model, identity and the caller authentication headers used by
  LADR-008. Tool keys are SHA-256 of the tool label.
- **What is recorded:** every known tool forwarded after a **completed** pass — every asked tool has a valid
  answer. A fail-open (full or partial) or invalid answer, the all-skip guard and the small-catalog skip record
  nothing, so a classifier outage cannot lock the whole catalog in. History-protected and always-kept tools are
  recorded too, so a harness that compacts away a tool call does not lose that tool mid-session.
- **Bounds:** the same bounded session memory as HLD 001 LADR-008 (`SessionScopedMemory`) — 256 scopes, LRU eviction, 30-minute idle expiry,
  lost on restart; at most 1,024 tool keys per scope, beyond which nothing new is recorded.
- **Shared memory extension (HLD 017 LADR-05):** requires a per-instance lifetime and an uncapped mode on the future HLD 017 change — `SessionScopedMemory` currently offers only fixed 30-minute / 256-scope constants, so a per-instance TTL and uncapped mode are a requirement for that future change, not a shipped feature. Tool ordering and sticky tools keep 30 minutes and 256 scopes; the HLD 017 checksum cache uses its own instance with `LLM_MESSAGES_CACHE_TTL_HOURS` (default and maximum 120 hours) and no cap.

## Consequences

- Within a session the forwarded catalog only grows, so the tool prefix stays stable until the harness adds or
  drops a tool. The cost is tokens: per-request tool tokens drift toward the harness catalog over a long session.
- The per-question memo (LADR-07) still saves classifier work; sticky tools additionally skip classification.
- Tools sharing a label are kept together (conservative). Subagents that share a session id share one set.
- Real upstream cache-hit gains are unmeasured; this only removes the filter as a source of prefix churn.

## Amendment (2026-10-28)

[LADR-13](LADR-13-sticky-selection-requires-ordering.md) makes the dependency behind this decision's prefix-stability
consequence explicit. Membership stabilises **which** tools are forwarded, not their order: the filter prunes in
place and keeps the caller's array order, so a harness that re-sends the same tools in a different order still
changes the forwarded `tools` prefix unless HLD 001 LADR-008 ordering is enabled. The Host therefore refuses to
start when `Imposter:DecisionsTools:Enabled` and `StickyPerSession` are both true and `Imposter:ToolOrdering:Enabled`
is false. Recording, scope, bounds and the fail-open recording rules above are unchanged by that amendment.
