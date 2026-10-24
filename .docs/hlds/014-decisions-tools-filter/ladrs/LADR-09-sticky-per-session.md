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

## Consequences

- Within a session the forwarded catalog only grows, so the tool prefix stays stable until the harness adds or
  drops a tool. The cost is tokens: per-request tool tokens drift toward the harness catalog over a long session.
- The per-question memo (LADR-07) still saves classifier work; sticky tools additionally skip classification.
- Tools sharing a label are kept together (conservative). Subagents that share a session id share one set.
- Real upstream cache-hit gains are unmeasured; this only removes the filter as a source of prefix churn.
