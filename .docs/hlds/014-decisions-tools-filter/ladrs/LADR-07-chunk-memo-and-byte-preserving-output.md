# LADR-07: In-memory chunk memo, byte-preserving output and deferred-loading skip

**Status:** Accepted

## Context

- An agent turn issues many upstream requests with the same human request; each paid the full classification (4.9–8.7 s for 56 tools on `kev-4b`).
- Tools lead the upstream prompt-cache prefix; a kept set that differs between agent steps invalidates the cached system prompt and conversation each step.
- The classifier is deterministic for identical input (max |ΔP| 0.0002 across repeats).
- Re-serializing the whole body re-encodes message text (for example `<` as `\u003C`), although only the tool catalog changes.
- Claude Code's tool search sends a `ToolSearch` gateway and a `defer_loading` placeholder described as "never call"; the classifier scores the placeholder about 0.13.

## Decision

**Memoise** each chunk's validated `use` probabilities in process memory, keyed by a SHA-256 of the exact classifier request (model, state, questions), bounded to 1,024 entries with oldest-first eviction. Never cache a failed or invalid chunk. The threshold is applied after the lookup.

**Rewrite** only the top-level `tools` and `tool_choice` values of a pruned body; every other top-level property is copied from the original bytes.

**Skip** the pass for a catalog containing any `defer_loading: true` tool (including namespace children) or a `tool_search` tool.

## Consequences

- Later agent steps of a user turn reuse answers and forward the same tool set; a new human message re-classifies once.
- The memo is per process and lost on restart; a decision model swapped under the same id keeps stale answers until restart.
- Messages, system and metadata reach the upstream exactly as the client sent them; the pruned `tools` array is re-encoded but JSON-equivalent.
- Deferred-loading catalogs are never pruned.
